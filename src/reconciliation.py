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

from src.models.order_state import OrderState
from src.state_store import OrderIntentRow, StateStore
from src.utils import parse_optional_float

logger = logging.getLogger(__name__)


class ReconciliationError(Exception):
    """Raised when reconciliation finds discrepancies."""

    pass


def compare_order_states(
    sqlite_orders: List[OrderIntentRow], alpaca_orders: List[Dict[str, Any]]
) -> tuple[list[dict[str, object]], int]:
    """Compare SQLite vs Alpaca orders.

    Returns: (discrepancies, safe_updates_count)
    - discrepancies: List of mismatches (Rules 2-3)
    - safe_updates_count: Count of safe terminal updates (Rule 1)
    """
    discrepancies: list[dict[str, object]] = []
    safe_updates = 0
    alpaca_order_ids: dict[str, Any] = {o["client_order_id"]: o for o in alpaca_orders}

    # Rule 1: Alpaca has order terminal, SQLite has non-terminal → UPDATE SQLite (safe)
    # Count these as safe updates
    for order in alpaca_orders:
        client_id = order["client_order_id"]
        alpaca_status = order["status"]

        if client_id in {o["client_order_id"] for o in sqlite_orders}:
            sqlite_order: Any = next(o for o in sqlite_orders if o["client_order_id"] == client_id)
            sqlite_status = str(sqlite_order.get("status") or "unknown")
            sqlite_state = OrderState.from_alpaca(sqlite_status)
            alpaca_state = OrderState.from_alpaca(alpaca_status)

            if alpaca_state.is_terminal and sqlite_state.has_fill_potential:
                safe_updates += 1

    # Rule 2: SQLite has order terminal, Alpaca reports non-terminal → DISCREPANCY
    for sqlite_order_item in sqlite_orders:
        client_id = str(sqlite_order_item["client_order_id"])
        sqlite_status_check: str = str(sqlite_order_item.get("status") or "")
        sqlite_state_check = OrderState.from_alpaca(sqlite_status_check)

        if sqlite_state_check.is_terminal:
            if client_id not in alpaca_order_ids:
                # Order is terminal locally, not in Alpaca (possible timeout/already cleared)
                continue

            alpaca_status_from_ids: str = alpaca_order_ids[client_id].get("status") or ""
            alpaca_state_from_ids = OrderState.from_alpaca(alpaca_status_from_ids)
            if alpaca_state_from_ids.has_fill_potential:
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

    return discrepancies, safe_updates


def apply_safe_order_updates(
    state_store: StateStore,
    sqlite_orders: List[OrderIntentRow],
    alpaca_orders: List[Dict[str, Any]],
) -> int:
    """Apply Rule 1 updates (Alpaca terminal → SQLite).

    Returns: Count of orders updated
    """
    updated = 0
    for order in alpaca_orders:
        client_id = order["client_order_id"]
        alpaca_status = order["status"]

        if client_id in {o["client_order_id"] for o in sqlite_orders}:
            sqlite_order: Any = next(o for o in sqlite_orders if o["client_order_id"] == client_id)
            sqlite_status = str(sqlite_order.get("status") or "unknown")
            sqlite_state = OrderState.from_alpaca(sqlite_status)
            alpaca_state = OrderState.from_alpaca(alpaca_status)

            if alpaca_state.is_terminal and sqlite_state.has_fill_potential:
                # Update SQLite to match Alpaca (Alpaca is authoritative for terminal states)
                logger.info(f"Updating {client_id}: {sqlite_status} → {alpaca_status}")
                state_store.update_order_intent(
                    client_order_id=client_id,
                    status=alpaca_status or "unknown",
                    filled_qty=parse_optional_float(order.get("filled_qty")),
                    alpaca_order_id=order.get("id"),
                )
                updated += 1

    return updated


def compare_positions(
    sqlite_positions: Dict[str, Dict[str, Any]], alpaca_positions: List[Dict[str, Any]]
) -> list[dict[str, object]]:
    """Compare SQLite vs Alpaca positions (Rule 4).

    Returns: List of position mismatches
    """
    discrepancies: list[dict[str, object]] = []
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

    return discrepancies


async def reconcile(broker: Any, state_store: StateStore) -> None:
    """Reconcile state with Alpaca on startup.

    Args:
        broker: Broker client
        state_store: State store

    Raises:
        ReconciliationError if discrepancies found
    """
    # Get state from both sources (await async broker methods)
    try:
        alpaca_orders = cast(List[Dict[str, Any]], await broker.get_open_orders())
        alpaca_positions = cast(List[Dict[str, Any]], await broker.get_positions())
        sqlite_orders = state_store.get_all_order_intents()

        logger.info(
            f"Reconciling: {len(alpaca_orders)} open orders in Alpaca, {len(sqlite_orders)} in SQLite"
        )
    except (ConnectionError, TimeoutError) as e:
        raise ReconciliationError(f"Failed to fetch state for reconciliation: {e}")

    # Apply safe updates (Rule 1)
    apply_safe_order_updates(state_store, sqlite_orders, alpaca_orders)

    # Check for order discrepancies (Rules 2-3)
    order_discrepancies, _ = compare_order_states(sqlite_orders, alpaca_orders)
    discrepancies = order_discrepancies

    # Rule 4: Position mismatch → DISCREPANCY
    # Get last recorded positions from positions_snapshot table
    with sqlite3.connect(state_store.db_path) as conn:
        cursor = conn.cursor()

        # Get latest positions snapshot
        cursor.execute("""
            SELECT symbol, qty, avg_entry_price FROM positions_snapshot
            WHERE timestamp_utc = (SELECT MAX(timestamp_utc) FROM positions_snapshot)
        """)
        sqlite_positions_rows = cursor.fetchall()

    sqlite_positions = {
        row[0]: {"qty": row[1], "avg_entry_price": row[2]} for row in sqlite_positions_rows
    }

    # Compare positions using helper
    position_discrepancies = compare_positions(sqlite_positions, alpaca_positions)
    discrepancies.extend(position_discrepancies)

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

    now_utc = datetime.now(timezone.utc).isoformat()

    with sqlite3.connect(state_store.db_path) as conn:
        cursor = conn.cursor()

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

    logger.info(f"Reconciliation complete: {len(alpaca_positions)} positions recorded")


async def reconcile_fills(
    broker: Any,
    state_store: StateStore,
    on_order_update: Any = None,
) -> int:
    """Reconcile fill quantities on startup.

    Compares broker order filled_qty to DB order_intents.filled_qty for all
    non-terminal orders. If broker > DB, synthesises an order update event and
    passes it through the handler to insert fill rows and apply deltas.

    Args:
        broker: Broker client (async)
        state_store: State store
        on_order_update: Async handler for synthesised order update events

    Returns:
        Number of fill reconciliation events synthesised
    """
    synthesised_count = 0
    try:
        all_intents = state_store.get_all_order_intents()
        # Only reconcile orders that could have fills we missed
        # Filter out empty/unknown statuses to avoid unnecessary broker API calls
        non_terminal = [
            o
            for o in all_intents
            if (status := str(o.get("status") or "").strip().lower())
            and status != "unknown"
            and OrderState.from_alpaca(status).has_fill_potential
            and o.get("alpaca_order_id")
        ]

        if not non_terminal:
            logger.info("Fill reconciliation: no non-terminal orders to reconcile")
            return 0

        logger.info("Fill reconciliation: checking %d non-terminal orders", len(non_terminal))

        # Fetch all open orders from broker
        try:
            broker_orders = cast(List[Dict[str, Any]], await broker.get_open_orders())
        except Exception as e:
            logger.error("Fill reconciliation: failed to fetch broker orders: %s", e)
            return 0

        broker_order_map: Dict[str, Dict[str, Any]] = {}
        for bo in broker_orders:
            cid = bo.get("client_order_id")
            aid = bo.get("id")
            if cid:
                broker_order_map[cid] = bo
            if aid:
                broker_order_map[str(aid)] = bo

        for intent in non_terminal:
            try:
                client_id = str(intent["client_order_id"])
                alpaca_id = str(intent.get("alpaca_order_id") or "")
                db_filled_qty = float(intent.get("filled_qty") or 0)

                # Look up broker order
                broker_order = broker_order_map.get(client_id) or broker_order_map.get(alpaca_id)
                if not broker_order:
                    # Order may have transitioned to terminal status and dropped from
                    # open orders. Try to fetch individual order once if broker supports it.
                    fetch_id = alpaca_id or client_id
                    if fetch_id and hasattr(broker, "get_order"):
                        get_order = getattr(broker, "get_order")
                        if callable(get_order):
                            try:
                                broker_order = cast(Dict[str, Any], await get_order(fetch_id))
                            except Exception as fetch_err:
                                logger.warning(
                                    "Fill reconciliation: failed to fetch order %s from broker: %s",
                                    fetch_id,
                                    fetch_err,
                                )

                if not broker_order:
                    logger.debug(
                        "Fill reconciliation: order %s not found among open or directly fetched orders; skipping",
                        client_id,
                    )
                    continue

                broker_filled_qty = parse_optional_float(broker_order.get("filled_qty")) or 0.0
                broker_avg_price = parse_optional_float(broker_order.get("filled_avg_price"))
                broker_status = str(broker_order.get("status", "unknown")).lower()

                if broker_filled_qty > db_filled_qty + 1e-9:
                    logger.warning(
                        "Fill reconciliation drift: order=%s broker_qty=%.4f db_qty=%.4f "
                        "— synthesising update",
                        client_id,
                        broker_filled_qty,
                        db_filled_qty,
                    )

                    if on_order_update is not None:
                        from src.stream_polling import OrderUpdateWrapper

                        # Synthesise an OrderUpdateWrapper that mimics a stream event
                        synth_order_data = {
                            "id": alpaca_id,
                            "client_order_id": client_id,
                            "symbol": intent.get("symbol", ""),
                            "side": intent.get("side", "unknown"),
                            "status": broker_status,
                            "filled_qty": broker_filled_qty,
                            "filled_avg_price": broker_avg_price,
                        }
                        synth_event = OrderUpdateWrapper(synth_order_data)
                        await on_order_update(synth_event)

                    synthesised_count += 1

            except Exception as e:
                logger.error(
                    "Fill reconciliation error for order %s: %s",
                    intent.get("client_order_id", "?"),
                    e,
                )

    except Exception as e:
        logger.error("Fill reconciliation failed: %s", e)

    if synthesised_count > 0:
        logger.info("Fill reconciliation: synthesised %d update(s)", synthesised_count)
    else:
        logger.info("Fill reconciliation: all fills in sync")

    return synthesised_count
