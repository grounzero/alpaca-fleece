"""RuntimeReconciler: continuous truth repair loop for SQLite/in-memory state.

Periodically reconciles Alpaca positions, open orders, and recent orders against
SQLite "order intents" and internal state. Runs every 30-300 seconds (configurable)
and immediately after polling error bursts.

Key repairs:
- Rule 1 updates (Alpaca terminal → SQLite)
- Stuck pending_exit flag clearing
- Trading halt on discrepancies
"""

import asyncio
import json
import logging
import sqlite3
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

from src.async_broker_adapter import AsyncBrokerInterface
from src.event_bus import EventBus
from src.models.order_state import OrderState
from src.position_tracker import PositionTracker
from src.reconciliation import (
    apply_safe_order_updates,
    compare_order_states,
    compare_positions,
)
from src.state_store import OrderIntentRow, StateStore

logger = logging.getLogger(__name__)


class RuntimeReconcilerError(Exception):
    """Raised when runtime reconciler encounters errors."""

    pass


class RuntimeReconciler:
    """Periodic reconciliation of SQLite/in-memory state against broker truth."""

    def __init__(
        self,
        broker: AsyncBrokerInterface,
        state_store: StateStore,
        position_tracker: PositionTracker,
        event_bus: Optional[EventBus] = None,
        check_interval_seconds: int = 120,
        repair_stuck_exits: bool = True,
        halt_on_discrepancy: bool = True,
        broker_timeout_seconds: int = 30,
    ) -> None:
        """Initialise runtime reconciler.

        Args:
            broker: Broker client for fetching live state
            state_store: State store for SQLite queries
            position_tracker: Position tracker for clearing stuck flags
            event_bus: Optional event bus for publishing events
            check_interval_seconds: How often to run reconciliation (30-300s)
            repair_stuck_exits: Auto-clear stuck pending_exit flags
            halt_on_discrepancy: Set trading_halted=True on discrepancies
            broker_timeout_seconds: Timeout for broker API calls
        """
        self.broker = broker
        self.state_store = state_store
        self.position_tracker = position_tracker
        self.event_bus = event_bus
        self.check_interval_seconds = max(30, min(300, check_interval_seconds))
        self.repair_stuck_exits = repair_stuck_exits
        self.halt_on_discrepancy = halt_on_discrepancy
        self.broker_timeout_seconds = broker_timeout_seconds

        self._running = False
        self._monitor_task: Optional[asyncio.Task[None]] = None
        self._consecutive_failures = 0
        self._max_consecutive_failures = 3

    async def start(self) -> None:
        """Start the runtime reconciler monitoring loop."""
        if self._running:
            logger.warning("Runtime reconciler already running")
            return

        logger.info("Starting runtime reconciler...")
        logger.info(f"  Check interval: {self.check_interval_seconds}s")
        logger.info(f"  Repair stuck exits: {self.repair_stuck_exits}")
        logger.info(f"  Halt on discrepancy: {self.halt_on_discrepancy}")

        self._running = True
        self._monitor_task = asyncio.create_task(self._monitor_loop())
        logger.info("Runtime reconciler started")

    async def stop(self) -> None:
        """Stop the runtime reconciler monitoring loop."""
        if not self._running:
            return

        logger.info("Stopping runtime reconciler...")
        self._running = False

        if self._monitor_task:
            self._monitor_task.cancel()
            try:
                await self._monitor_task
            except asyncio.CancelledError:
                pass

        logger.info("Runtime reconciler stopped")

    async def _monitor_loop(self) -> None:
        """Main monitoring loop."""
        logger.info("Runtime reconciler monitor loop started")

        while self._running:
            try:
                await self._run_reconciliation_check()
            except Exception as e:
                logger.error(f"Error in runtime reconciler loop: {e}", exc_info=True)

            try:
                await asyncio.sleep(self.check_interval_seconds)
            except asyncio.CancelledError:
                break

        logger.info("Runtime reconciler monitor loop ended")

    async def _run_reconciliation_check(self) -> Dict[str, object]:
        """Run a single reconciliation check.

        Returns: Report dict with status, discrepancies, repairs
        """
        start_time = datetime.now(timezone.utc)
        report: Dict[str, object] = {
            "timestamp_utc": start_time.isoformat(),
            "check_type": "runtime",
            "status": "unknown",
            "discrepancies": [],
            "repairs": [],
            "error_message": None,
        }

        try:
            # Fetch broker state with timeout
            broker_state = await self._fetch_broker_state()
            if broker_state is None:
                # Broker unavailable - set degraded health and return
                self._consecutive_failures += 1
                self.state_store.set_state("broker_health", "degraded")
                report["status"] = "broker_unavailable"
                report["error_message"] = "Broker API unavailable or timed out"
                duration_ms = int((datetime.now(timezone.utc) - start_time).total_seconds() * 1000)
                report["duration_ms"] = duration_ms
                self._persist_report(report)
                logger.warning(
                    "Runtime reconciliation: broker unavailable (attempt %d)",
                    self._consecutive_failures,
                )
                return report

            alpaca_orders = broker_state["orders"]
            alpaca_positions = broker_state["positions"]
            sqlite_orders: List[OrderIntentRow] = self.state_store.get_all_order_intents()

            # Reset broker health to healthy
            self.state_store.set_state("broker_health", "healthy")

            # Apply safe updates (Rule 1)
            safe_updates = apply_safe_order_updates(self.state_store, sqlite_orders, alpaca_orders)

            # Check for order discrepancies (Rules 2-3)
            order_discrepancies, _ = compare_order_states(sqlite_orders, alpaca_orders)

            # Check for position discrepancies (Rule 4)
            # Get latest positions from positions_snapshot
            with sqlite3.connect(self.state_store.db_path) as conn:
                cursor = conn.cursor()
                cursor.execute("""
                    SELECT symbol, qty, avg_entry_price FROM positions_snapshot
                    WHERE timestamp_utc = (SELECT MAX(timestamp_utc) FROM positions_snapshot)
                """)
                sqlite_positions_rows = cursor.fetchall()
            sqlite_positions = {
                row[0]: {"qty": row[1], "avg_entry_price": row[2]} for row in sqlite_positions_rows
            }
            position_discrepancies = compare_positions(sqlite_positions, alpaca_positions)

            # Combine all discrepancies
            all_discrepancies = order_discrepancies + position_discrepancies

            # Check for stuck pending_exit flags
            stuck_exits: List[Dict[str, Any]] = []
            if self.repair_stuck_exits:
                stuck_exits = self._check_stuck_pending_exits(alpaca_orders, alpaca_positions)

            # Repair stuck exits
            repairs: List[Dict[str, Any]] = []
            for stuck in stuck_exits:
                self._repair_stuck_exit(stuck["symbol"])
                repairs.append(
                    {
                        "type": "stuck_pending_exit",
                        "symbol": stuck["symbol"],
                        "action": "cleared_pending_exit_flag",
                    }
                )

            # Set trading_halted if discrepancies found
            if all_discrepancies and self.halt_on_discrepancy:
                self.state_store.set_state("trading_halted", "true")
                logger.critical(
                    "Runtime reconciliation: %d discrepancies found - trading halted",
                    len(all_discrepancies),
                )
            elif not all_discrepancies:
                # Clear halt if no discrepancies (auto-recovery)
                current_halt = self.state_store.get_state("trading_halted")
                if current_halt == "true":
                    self.state_store.set_state("trading_halted", "false")
                    logger.info("Runtime reconciliation: clean check - trading halt cleared")

            # Update report
            report["status"] = "clean" if not all_discrepancies else "discrepancies_found"
            report["discrepancies"] = all_discrepancies
            report["repairs"] = repairs
            report["safe_updates_count"] = safe_updates

            # Reset consecutive failures on success
            self._consecutive_failures = 0

            # Log summary
            if all_discrepancies or repairs:
                logger.warning(
                    "Runtime reconciliation: %d discrepancies, %d repairs, %d safe updates",
                    len(all_discrepancies),
                    len(repairs),
                    safe_updates,
                )
            else:
                logger.debug(
                    "Runtime reconciliation: clean (0 discrepancies, %d safe updates)", safe_updates
                )

        except Exception as e:
            self._consecutive_failures += 1
            report["status"] = "error"
            report["error_message"] = str(e)
            logger.error("Runtime reconciliation check failed: %s", e, exc_info=True)

            # Degrade to warning-only mode after max consecutive failures
            if self._consecutive_failures >= self._max_consecutive_failures:
                logger.warning(
                    "Runtime reconciler: %d consecutive failures - degraded to warning-only mode",
                    self._consecutive_failures,
                )

        # Calculate duration and persist report
        duration_ms = int((datetime.now(timezone.utc) - start_time).total_seconds() * 1000)
        report["duration_ms"] = duration_ms
        self._persist_report(report)

        # Update last check timestamp
        self.state_store.set_state("reconciler_last_check_utc", start_time.isoformat())
        self.state_store.set_state(
            "reconciler_consecutive_failures", str(self._consecutive_failures)
        )

        return report

    async def _fetch_broker_state(self) -> Optional[Dict[str, List[Dict[str, Any]]]]:
        """Fetch broker state with timeout.

        Returns: Dict with "orders" and "positions" keys, or None if unavailable
        """
        try:
            # Use asyncio.wait_for to enforce timeout
            orders = await asyncio.wait_for(
                self.broker.get_open_orders(), timeout=self.broker_timeout_seconds
            )
            positions = await asyncio.wait_for(
                self.broker.get_positions(), timeout=self.broker_timeout_seconds
            )
            return {
                "orders": orders,
                "positions": positions,
            }
        except (asyncio.TimeoutError, ConnectionError, Exception) as e:
            logger.warning("Failed to fetch broker state: %s", e)
            return None

    def _check_stuck_pending_exits(
        self, alpaca_orders: List[Dict[str, Any]], alpaca_positions: List[Dict[str, Any]]
    ) -> List[Dict[str, Any]]:
        """Check for stuck pending_exit flags.

        Returns: List of stuck positions with details
        """
        stuck: List[Dict[str, Any]] = []

        # Query positions with pending_exit=True
        conn = sqlite3.connect(self.state_store.db_path)
        cursor = conn.cursor()
        cursor.execute("""
            SELECT symbol, side FROM position_tracking
            WHERE pending_exit = 1
        """)
        pending_exits = cursor.fetchall()
        conn.close()

        if not pending_exits:
            return stuck

        # Build alpaca state maps for quick lookups
        alpaca_position_symbols = {p["symbol"] for p in alpaca_positions}
        alpaca_order_map: Dict[str, List[Dict[str, Any]]] = {}
        for order in alpaca_orders:
            symbol = order.get("symbol", "")
            if symbol not in alpaca_order_map:
                alpaca_order_map[symbol] = []
            alpaca_order_map[symbol].append(order)

        for symbol, side in pending_exits:
            # Check if position still exists in broker
            if symbol not in alpaca_position_symbols:
                # Position closed in broker but pending_exit still set - this is stuck
                stuck.append(
                    {
                        "symbol": symbol,
                        "side": side,
                        "reason": "position_closed_but_flag_set",
                    }
                )
                continue

            # Check if there's a working exit order in SQLite
            conn_check = sqlite3.connect(self.state_store.db_path)
            cursor_check = conn_check.cursor()
            cursor_check.execute(
                """
                SELECT status FROM order_intents
                WHERE symbol = ? AND side != ?
                  AND status IN ('new', 'submitted', 'accepted', 'partially_filled', 'pending_new')
                """,
                (symbol, side),
            )
            sqlite_exit_orders = cursor_check.fetchall()
            conn_check.close()

            if sqlite_exit_orders:
                # Working exit order exists - not stuck
                continue

            # Check if there's a working exit order in Alpaca
            alpaca_symbol_orders = alpaca_order_map.get(symbol, [])
            has_working_exit = False
            for order in alpaca_symbol_orders:
                order_side = order.get("side", "").lower()
                order_status = order.get("status", "").lower()
                state = OrderState.from_alpaca(order_status)

                # Exit order has opposite side (buy exits short, sell exits long)
                is_exit_order = (side == "long" and order_side == "sell") or (
                    side == "short" and order_side == "buy"
                )

                if is_exit_order and state.has_fill_potential:
                    has_working_exit = True
                    break

            if not has_working_exit:
                # No working exit order in SQLite or Alpaca - this is stuck
                stuck.append(
                    {
                        "symbol": symbol,
                        "side": side,
                        "reason": "no_working_exit_order",
                    }
                )

        return stuck

    def _repair_stuck_exit(self, symbol: str) -> None:
        """Clear stuck pending_exit flag.

        Args:
            symbol: Symbol to repair
        """
        position = self.position_tracker.get_position(symbol)
        if position is None:
            logger.warning("Cannot repair stuck exit for %s: position not found in tracker", symbol)
            return

        position.pending_exit = False
        self.position_tracker.upsert_position(position)

        logger.warning("Repaired stuck pending_exit: symbol=%s", symbol)

    def _persist_report(self, report: Dict[str, object]) -> None:
        """Persist reconciliation report to database.

        Args:
            report: Report dict with status, discrepancies, repairs
        """
        try:
            with sqlite3.connect(self.state_store.db_path) as conn:
                cursor = conn.cursor()

                discrepancies = report.get("discrepancies", [])
                repairs = report.get("repairs", [])
                discrepancies_json = json.dumps(discrepancies)
                repairs_json = json.dumps(repairs)

                discrepancies_count = len(discrepancies) if isinstance(discrepancies, list) else 0
                repairs_count = len(repairs) if isinstance(repairs, list) else 0

                cursor.execute(
                    """
                    INSERT INTO reconciliation_reports
                    (timestamp_utc, check_type, duration_ms, discrepancies_count, repaired_count,
                     status, discrepancies_json, repairs_json, error_message)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        report.get("timestamp_utc"),
                        report.get("check_type"),
                        report.get("duration_ms", 0),
                        discrepancies_count,
                        repairs_count,
                        report.get("status"),
                        discrepancies_json,
                        repairs_json,
                        report.get("error_message"),
                    ),
                )

                conn.commit()

        except Exception as e:
            logger.error("Failed to persist reconciliation report: %s", e)
