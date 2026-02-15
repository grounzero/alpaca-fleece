"""Housekeeping - periodic maintenance tasks.

Responsibilities:
- Snapshot equity every 60s
- Reset daily counters at 09:30 ET
- Log periodic health status
"""

import asyncio
import inspect
import logging
import sqlite3
import uuid
from datetime import datetime, timezone
from typing import TYPE_CHECKING, Optional, Protocol

import pytz

from src.async_broker_adapter import AsyncBrokerInterface
from src.order_manager import OrderManagerError
from src.state_store import StateStore
from src.utils import parse_optional_float

if TYPE_CHECKING:
    from src.order_manager import OrderManager


class AlertNotifierProtocol(Protocol):
    async def send_alert_async(
        self, title: str, message: str, severity: str
    ) -> None:  # pragma: no cover - typing only
        ...


logger = logging.getLogger(__name__)

ET = pytz.timezone("America/New_York")


class Housekeeping:
    """Periodic maintenance."""

    def __init__(self, broker: AsyncBrokerInterface, state_store: StateStore) -> None:
        """Initialise housekeeping.

        Args:
            broker: Broker client
            state_store: State store
        """
        self.broker = broker  # type: AsyncBrokerInterface
        self.state_store = state_store
        self.running = False
        # These may be wired after initialisation by Orchestrator
        # `order_manager` is set later by Orchestrator; use a forward-annotated
        # type to satisfy strict mypy without importing at runtime.
        self.order_manager: "OrderManager" | None = None
        # `notifier` implements a lightweight protocol used by Housekeeping
        # for async alerting. Use the protocol type to avoid runtime import
        # dependency cycles while keeping static typing strict.
        self.notifier: AlertNotifierProtocol | None = None

    async def start(self) -> None:
        """Start housekeeping tasks."""
        logger.info("Housekeeping.start() entered")
        self.running = True

        # Run equity snapshot and daily reset tasks
        tasks = [
            asyncio.create_task(self._equity_snapshots(), name="equity_snapshots"),
            asyncio.create_task(self._daily_resets(), name="daily_resets"),
        ]

        try:
            logger.info("Housekeeping: waiting on internal gather...")
            # Use return_exceptions=True to prevent one crashing task from killing both
            results = await asyncio.gather(*tasks, return_exceptions=True)

            # CRITICAL: If we reach here, one of the housekeeping tasks exited
            logger.critical("CRITICAL: Housekeeping internal gather returned - a task exited!")

            # Check for exceptions and log them
            for i, result in enumerate(results):
                task_name = "_equity_snapshots" if i == 0 else "_daily_resets"
                if isinstance(result, Exception):
                    logger.error(f"Housekeeping task {task_name} failed: {result}", exc_info=result)
                else:
                    logger.critical(f"Housekeeping task {task_name} returned normally: {result}")

        except asyncio.CancelledError:
            logger.info("Housekeeping.start() cancelled")
            self.running = False
            raise  # Re-raise to properly handle shutdown

        logger.info("Housekeeping.start() exiting - this should NOT happen during normal operation")

    async def stop(self) -> None:
        """Stop housekeeping tasks."""
        self.running = False

    async def graceful_shutdown(self, skip_cancel: bool = False) -> None:
        """Graceful shutdown: cancel orders, close positions, save state (Tier 1).

        Called on SIGTERM/SIGINT to safely exit.
        """
        logger.info("Graceful shutdown initiated...")

        try:
            # Step 1: Cancel all open orders (skippable)
            if not skip_cancel:
                logger.info("Cancelling open orders...")
                try:
                    maybe_orders = self.broker.get_open_orders()
                    orders = (
                        await maybe_orders if asyncio.iscoroutine(maybe_orders) else maybe_orders
                    )
                    for order in orders:
                        order_id = order.get("id")
                        if order_id:
                            maybe_cancel = self.broker.cancel_order(order_id)
                            if inspect.isawaitable(maybe_cancel):
                                await maybe_cancel
                            # Invalidate relevant caches after cancelling an order
                            if hasattr(self.broker, "invalidate_cache"):
                                try:
                                    maybe_inv = self.broker.invalidate_cache(
                                        "get_positions", "get_open_orders"
                                    )
                                    if inspect.isawaitable(maybe_inv):
                                        await maybe_inv
                                except Exception:
                                    logger.debug(
                                        "invalidate_cache failed after cancel", exc_info=True
                                    )
                            logger.info(f"Cancelled order: {order_id}")
                except (ConnectionError, TimeoutError) as e:
                    logger.error(f"Failed to cancel orders: {e}")

            # Step 2: Close/flatten open positions via OrderManager (preferred)
            logger.info("Closing open positions (shutdown flatten)...")
            try:
                if self.order_manager is None:
                    # No order manager wired - fall back to direct broker submits (legacy)
                    logger.warning(
                        "OrderManager not wired into Housekeeping; using legacy submit behavior"
                    )
                    maybe_positions = self.broker.get_positions()
                    positions = (
                        await maybe_positions
                        if inspect.isawaitable(maybe_positions)
                        else maybe_positions
                    )
                    for position in positions:
                        symbol = position["symbol"]
                        qty = float(position["qty"])
                        side = "sell" if qty > 0 else "buy"
                        abs_qty = abs(qty)
                        client_order_id = f"close_{symbol}_{datetime.now(timezone.utc).isoformat()}"

                        logger.info(f"Closing position: {symbol} ({abs_qty} shares via {side})")
                        maybe_submit = self.broker.submit_order(
                            symbol=symbol,
                            side=side,
                            qty=abs_qty,
                            client_order_id=client_order_id,
                            order_type="market",
                        )
                        if inspect.isawaitable(maybe_submit):
                            await maybe_submit
                        # Invalidate caches after submitting a close order
                        if hasattr(self.broker, "invalidate_cache"):
                            try:
                                maybe_inv2 = self.broker.invalidate_cache(
                                    "get_positions", "get_open_orders"
                                )
                                if inspect.isawaitable(maybe_inv2):
                                    await maybe_inv2
                            except Exception:
                                logger.debug("invalidate_cache failed after submit", exc_info=True)
                else:
                    # Use OrderManager to perform deterministic, idempotent flattening
                    # Use a UUID-based session id to ensure uniqueness even
                    # across rapid successive shutdown attempts.
                    shutdown_session_id = uuid.uuid4().hex
                    try:
                        summary = await self.order_manager.flatten_positions(shutdown_session_id)
                    except OrderManagerError as e:
                        # Critical failure starting flatten; notify and re-raise so
                        # callers can treat this as a hard failure (e.g. set exit codes).
                        logger.exception("Shutdown flatten failed to start (critical): %s", e)
                        if self.notifier is not None:
                            try:
                                if hasattr(self.notifier, "send_alert_async"):
                                    await self.notifier.send_alert_async(
                                        title="Shutdown flatten failed to start",
                                        message=str(e),
                                        severity="ERROR",
                                    )
                            except Exception:
                                logger.debug(
                                    "Notifier failed while reporting flatten start failure",
                                    exc_info=True,
                                )
                        raise
                    except Exception as e:
                        logger.exception("Shutdown flatten failed to start: %s", e)
                        # Alert and continue: attempt to determine remaining exposure
                        if self.notifier is not None:
                            try:
                                if hasattr(self.notifier, "send_alert_async"):
                                    await self.notifier.send_alert_async(
                                        title="Shutdown flatten failed to start",
                                        message=str(e),
                                        severity="ERROR",
                                    )
                            except Exception:
                                logger.debug(
                                    "Notifier failed while reporting flatten start failure",
                                    exc_info=True,
                                )

                        # Try to re-query positions to determine remaining exposure
                        remaining = []
                        try:
                            maybe_positions = self.broker.get_positions()
                            positions = (
                                await maybe_positions
                                if inspect.isawaitable(maybe_positions)
                                else maybe_positions
                            )
                            for p in positions:
                                q = parse_optional_float(p.get("qty")) or 0.0
                                if q != 0:
                                    remaining.append(p.get("symbol"))
                        except Exception:
                            logger.warning(
                                "Failed to re-query positions after flatten start failure; remaining exposure unknown"
                            )

                        summary = {
                            "submitted": [],
                            "failed": [{"error": str(e)}],
                            "remaining_exposure_symbols": remaining,
                        }

                    # Log summary
                    logger.info("Shutdown flatten summary: %s", summary)

                    # If any remaining exposure, emit critical alert and set exit code later
                    remaining = (
                        summary.get("remaining_exposure_symbols", [])
                        if isinstance(summary, dict)
                        else []
                    )
                    if remaining:
                        msg = f"Shutdown left remaining exposure on symbols: {remaining}"
                        logger.critical(msg)
                        if self.notifier is not None:
                            try:
                                if hasattr(self.notifier, "send_alert_async"):
                                    await self.notifier.send_alert_async(
                                        title="CRITICAL: Shutdown left exposure",
                                        message=msg,
                                        severity="CRITICAL",
                                    )
                            except Exception:
                                logger.debug(
                                    "Notifier failed while reporting remaining exposure",
                                    exc_info=True,
                                )
                        # raise a domain exception so the caller can decide how to handle exit codes
                        raise OrderManagerError(msg)
            except (ConnectionError, TimeoutError, OrderManagerError) as e:
                logger.error(f"Failed to close positions: {e}")

            # Step 3: Take final equity snapshot
            logger.info("Recording final state...")
            try:
                maybe_acc = self.broker.get_account()
                account = await maybe_acc if asyncio.iscoroutine(maybe_acc) else maybe_acc
                equity = account["equity"]

                import sqlite3

                with sqlite3.connect(self.state_store.db_path) as conn:
                    cursor = conn.cursor()
                    cursor.execute(
                        """INSERT INTO equity_curve (timestamp_utc, equity, daily_pnl)
                           VALUES (?, ?, ?)""",
                        (datetime.now(timezone.utc).isoformat(), equity, 0),
                    )
                    conn.commit()

                logger.info(f"Final equity snapshot: ${equity:.2f}")
            except (sqlite3.Error, ConnectionError) as e:
                logger.error(f"Failed to record final state: {e}")

            logger.info("Graceful shutdown complete")

        except (ConnectionError, TimeoutError) as e:
            logger.error(f"Graceful shutdown error: {e}")

    async def _equity_snapshots(self) -> None:
        """Snapshot equity every 60s."""
        logger.info("Equity snapshots task started")
        try:
            iteration = 0
            while self.running:
                iteration += 1
                if iteration % 5 == 0:  # Log every 5 iterations (5 minutes)
                    logger.info(f"Equity snapshots: iteration {iteration}, running={self.running}")
                try:
                    account = await self.broker.get_account()
                    equity = account["equity"]
                    daily_pnl = float(self.state_store.get_state("daily_pnl") or 0)

                    # Record to equity_curve table
                    import sqlite3

                    with sqlite3.connect(self.state_store.db_path) as conn:
                        cursor = conn.cursor()
                        cursor.execute(
                            """INSERT INTO equity_curve (timestamp_utc, equity, daily_pnl)
                               VALUES (?, ?, ?)""",
                            (datetime.now(timezone.utc).isoformat(), equity, daily_pnl),
                        )
                        conn.commit()

                    logger.debug(f"Equity snapshot: ${equity:.2f}")
                except asyncio.CancelledError:
                    raise  # Re-raise to handle graceful shutdown
                except (ConnectionError, TimeoutError, sqlite3.Error) as e:
                    logger.error(f"Equity snapshot failed: {e}")

                await asyncio.sleep(60)

            logger.critical("Equity snapshots: while loop exited - running became False!")

        except asyncio.CancelledError:
            logger.info("Equity snapshots task cancelled")
            raise
        except (ConnectionError, TimeoutError, sqlite3.Error) as e:
            logger.critical(f"Equity snapshots task crashed: {e}", exc_info=True)
            raise  # Propagate to be caught by gather
        finally:
            logger.info(f"Equity snapshots task exiting after {iteration} iterations")

    async def _daily_resets(self) -> None:
        """Reset daily counters at 09:30 ET."""
        logger.info("Daily resets task started")
        try:
            iteration = 0
            while self.running:
                iteration += 1
                if iteration % 10 == 0:  # Log every 10 iterations
                    logger.info(f"Daily resets: iteration {iteration}, running={self.running}")
                try:
                    # Check if it's 09:30 ET
                    now_et = datetime.now(ET)

                    # Only reset once per day
                    last_reset_date = self.state_store.get_state("daily_reset_date")
                    today = now_et.date().isoformat()

                    if now_et.hour == 9 and now_et.minute >= 30 and last_reset_date != today:
                        logger.info("Daily reset at 09:30 ET")
                        self.state_store.set_state("daily_trade_count", "0")
                        self.state_store.set_state("daily_pnl", "0")
                        self.state_store.set_state("daily_reset_date", today)

                except asyncio.CancelledError:
                    raise  # Re-raise to handle graceful shutdown
                except (sqlite3.Error, ValueError) as e:
                    logger.error(f"Daily reset failed: {e}")

                # Check every minute
                await asyncio.sleep(60)

            logger.critical("Daily resets: while loop exited - running became False!")

        except asyncio.CancelledError:
            logger.info("Daily resets task cancelled")
            raise
        except (sqlite3.Error, ValueError) as e:
            logger.critical(f"Daily resets task crashed: {e}", exc_info=True)
            raise  # Propagate to be caught by gather
        finally:
            logger.info(f"Daily resets task exiting after {iteration} iterations")
