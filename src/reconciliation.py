"""Reconciliation - startup verification of state vs Alpaca.

On startup, compare SQLite state with Alpaca's actual state.
If any discrepancy found: log, write report, refuse to start.
If clean: apply deterministic updates, proceed.

Terminal statuses: filled, canceled, expired, rejected (Alpaca API values)
Non-terminal statuses: new, submitted, accepted, partially_filled, pending_new, pending_cancel, pending_replace
"""

import json
import logging
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, cast

from src.state_store import StateStore
from src.utils import parse_optional_float

logger = logging.getLogger(__name__)


class ReconciliationError(Exception):
    """Raised when reconciliation finds discrepancies."""

    pass


TERMINAL_STATUSES = {"filled", "canceled", "expired", "rejected"}
NON_TERMINAL_STATUSES = {
    "new",
    "submitted",
    "accepted",
    "partially_filled",
    "pending_new",
    "pending_cancel",
    "pending_replace",
}


def reconcile(broker: Any, state_store: StateStore) -> None:
    """Reconcile state with Alpaca on startup.

    Args:
        broker: Broker client
        state_store: State store

    Raises:
        ReconciliationError if discrepancies found
    """
    discrepancies: list[dict[str, object]] = []

    # Helper: if a broker method returns a coroutine, run it to completion.
    def _resolve(maybe_coro: Any) -> Any:
        import asyncio

        if asyncio.iscoroutine(maybe_coro):
            try:
                loop = asyncio.get_running_loop()
            except RuntimeError:
                return asyncio.run(maybe_coro)
            else:
                # Running loop found - schedule and wait (should be rare in tests)
                return loop.run_until_complete(maybe_coro)
        return maybe_coro

    # Get state from both sources
    try:
        alpaca_orders = cast(List[Dict[str, Any]], _resolve(broker.get_open_orders()))
        alpaca_positions = cast(List[Dict[str, Any]], _resolve(broker.get_positions()))
        sqlite_orders = state_store.get_all_order_intents()

        logger.info(
            f"Reconciling: {len(alpaca_orders)} open orders in Alpaca, {len(sqlite_orders)} in SQLite"
        )
    except (ConnectionError, TimeoutError) as e:
        raise ReconciliationError(f"Failed to fetch state for reconciliation: {e}")

    # Check open orders
    alpaca_order_ids: dict[str, Any] = {o["client_order_id"]: o for o in alpaca_orders}

    # Rule 1: Alpaca has order terminal, SQLite has non-terminal → UPDATE SQLite (safe)
    for order in alpaca_orders:
        client_id = order["client_order_id"]
        alpaca_status = order["status"]

        if client_id in {o["client_order_id"] for o in sqlite_orders}:
            sqlite_order: Any = next(o for o in sqlite_orders if o["client_order_id"] == client_id)
            sqlite_status = str(sqlite_order.get("status") or "unknown")

            if alpaca_status in TERMINAL_STATUSES and sqlite_status in NON_TERMINAL_STATUSES:
                # Update SQLite to match Alpaca (Alpaca is authoritative for terminal states)
                logger.info(f"Updating {client_id}: {sqlite_status} → {alpaca_status}")
                state_store.update_order_intent(
                    client_order_id=client_id,
                    status=alpaca_status or "unknown",
                    filled_qty=parse_optional_float(order.get("filled_qty")),
                    alpaca_order_id=order.get("id"),
                )

    # Rule 2: SQLite has order terminal, Alpaca reports non-terminal → DISCREPANCY
    for sqlite_order_item in sqlite_orders:
        client_id = str(sqlite_order_item["client_order_id"])
        sqlite_status_check: str = str(sqlite_order_item.get("status") or "")

        if sqlite_status_check in TERMINAL_STATUSES:
            if client_id not in alpaca_order_ids:
                # Order is terminal locally, not in Alpaca (possible timeout/already cleared)
                continue

            alpaca_status_from_ids: str = alpaca_order_ids[client_id].get("status") or ""
            if alpaca_status_from_ids in NON_TERMINAL_STATUSES:
                discrepancies.append(
                    {
                        "type": "order_status_mismatch",
                        "client_order_id": client_id,
                        "sqlite_status": sqlite_status_check,
                        "alpaca_status": alpaca_status_from_ids,
                    }
                )

    # Rule 3: Alpaca has open orders not in SQLite → DISCREPANCY
    sqlite_client_ids = {o["client_order_id"] for o in sqlite_orders}
    for order in alpaca_orders:
        if order["client_order_id"] not in sqlite_client_ids:
            discrepancies.append(
                {
                    "type": "order_not_in_sqlite",
                    "client_order_id": order["client_order_id"],
                    "alpaca_details": order,
                }
            )

    # Rule 4: Position mismatch → DISCREPANCY
    # Get last recorded positions from positions_snapshot table
    conn = sqlite3.connect(state_store.db_path)
    cursor = conn.cursor()

    # Get latest positions snapshot
    cursor.execute("""
        SELECT symbol, qty, avg_entry_price FROM positions_snapshot
        WHERE timestamp_utc = (SELECT MAX(timestamp_utc) FROM positions_snapshot)
    """)
    sqlite_positions_rows = cursor.fetchall()
    conn.close()

    sqlite_positions = {
        row[0]: {"qty": row[1], "avg_entry_price": row[2]} for row in sqlite_positions_rows
    }

    # Compare positions
    alpaca_position_map = {p["symbol"]: p["qty"] for p in alpaca_positions}

    for symbol, sqlite_qty in sqlite_positions.items():
        alpaca_qty = alpaca_position_map.get(symbol, 0)
        if alpaca_qty != sqlite_qty["qty"]:
            discrepancies.append(
                {
                    "type": "position_mismatch",
                    "symbol": symbol,
                    "sqlite_qty": sqlite_qty["qty"],
                    "alpaca_qty": alpaca_qty,
                }
            )

    # Check for positions in Alpaca not in SQLite
    for symbol, alpaca_qty in alpaca_position_map.items():
        if symbol not in sqlite_positions and alpaca_qty != 0:
            discrepancies.append(
                {
                    "type": "position_not_in_sqlite",
                    "symbol": symbol,
                    "alpaca_qty": alpaca_qty,
                }
            )

    # If discrepancies found: write report and refuse
    if discrepancies:
        report = {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "discrepancies": discrepancies,
            "alpaca_orders": alpaca_orders,
            "sqlite_orders": sqlite_orders,
        }

        report_path = Path("data/reconciliation_error.json")
        report_path.parent.mkdir(parents=True, exist_ok=True)
        with open(report_path, "w") as f:
            json.dump(report, f, indent=2)

        logger.error(f"Reconciliation failed: {len(discrepancies)} discrepancies found")
        logger.error(f"Report written to {report_path}")
        raise ReconciliationError(f"Reconciliation found {len(discrepancies)} discrepancies")

    # Clean reconciliation: update positions snapshot
    logger.info("Reconciliation clean: updating positions snapshot")

    conn = sqlite3.connect(state_store.db_path)
    cursor = conn.cursor()

    now_utc = datetime.now(timezone.utc).isoformat()

    for position in alpaca_positions:
        cursor.execute(
            """INSERT INTO positions_snapshot (timestamp_utc, symbol, qty, avg_entry_price)
               VALUES (?, ?, ?, ?)""",
            (
                now_utc,
                position["symbol"],
                position["qty"],
                position["avg_entry_price"] or 0.0,
            ),
        )

    conn.commit()
    conn.close()

    logger.info(f"Reconciliation complete: {len(alpaca_positions)} positions recorded")
