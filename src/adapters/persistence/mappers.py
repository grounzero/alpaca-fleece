"""Persistence mappers: convert DB rows (tuple/sqlite3.Row/dict) to dataclasses.

These are pure functions and intentionally defensive about types so callers
can migrate incrementally.
"""

from __future__ import annotations

import sqlite3
from datetime import datetime
from typing import Any, Optional

from src.models.persistence import ExitAttempt, Fill, OrderIntent, Position
from src.models.persistence import PositionSnapshot
from src.utils import parse_optional_float


def _get(row: Any, idx: int, key: str) -> Any:
    """Helper to fetch by index or key from tuple/dict/sqlite3.Row/object."""
    # tuple/list-like
    if isinstance(row, (list, tuple)):
        try:
            return row[idx]
        except Exception:
            return None

    # sqlite3.Row supports mapping by key
    if isinstance(row, sqlite3.Row):
        try:
            return row[key]
        except Exception:
            # fallback to index access
            try:
                return row[idx]
            except Exception:
                return None

    # dict-like
    if isinstance(row, dict):
        return row.get(key)

    # arbitrary object: try attribute, then index
    if hasattr(row, key):
        return getattr(row, key)

    try:
        return row[idx]
    except Exception:
        return None


def _parse_iso(ts: Optional[Any]) -> Optional[datetime]:
    if ts is None:
        return None
    if isinstance(ts, datetime):
        return ts
    try:
        return datetime.fromisoformat(str(ts))
    except Exception:
        return None


def order_intent_from_row(row: Any) -> OrderIntent:
    """Map DB row (client_order_id, strategy, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id)

    Accepts tuple, sqlite3.Row, dict, or object with attributes.
    """
    client_order_id = _get(row, 0, "client_order_id") or ""
    strategy = _get(row, 1, "strategy")
    symbol = _get(row, 2, "symbol") or ""
    side = _get(row, 3, "side") or ""
    qty_raw = _get(row, 4, "qty")
    atr_raw = _get(row, 5, "atr")
    status = _get(row, 6, "status") or ""
    filled_qty_raw = _get(row, 7, "filled_qty")
    filled_avg_price_raw = _get(row, 8, "filled_avg_price")
    alpaca_order_id = _get(row, 9, "alpaca_order_id")

    qty = float(qty_raw) if qty_raw is not None else 0.0
    atr = parse_optional_float(atr_raw)
    filled_qty = parse_optional_float(filled_qty_raw)
    filled_avg_price = parse_optional_float(filled_avg_price_raw)

    return OrderIntent(
        client_order_id=str(client_order_id),
        strategy=str(strategy) if strategy is not None else None,
        symbol=str(symbol),
        side=str(side),
        qty=qty,
        atr=atr,
        status=str(status),
        filled_qty=filled_qty,
        filled_avg_price=filled_avg_price,
        alpaca_order_id=str(alpaca_order_id) if alpaca_order_id is not None else None,
    )


def order_intent_from_dict(d: dict[str, Any]) -> OrderIntent:
    """Map from an existing dict-shaped row."""
    return order_intent_from_row(d)


def position_from_row(row: Any) -> Position:
    """Map DB position_tracking row:
    (symbol, side, qty, entry_price, atr, entry_time, extreme_price, trailing_stop_price, trailing_stop_activated, pending_exit, updated_at)
    """
    symbol = _get(row, 0, "symbol") or ""
    side = _get(row, 1, "side") or ""
    qty_raw = _get(row, 2, "qty")
    entry_price_raw = _get(row, 3, "entry_price")
    atr_raw = _get(row, 4, "atr")
    entry_time_raw = _get(row, 5, "entry_time")
    extreme_price_raw = _get(row, 6, "extreme_price")
    trailing_stop_raw = _get(row, 7, "trailing_stop_price")
    trailing_stop_activated_raw = _get(row, 8, "trailing_stop_activated")
    pending_exit_raw = _get(row, 9, "pending_exit")
    updated_at_raw = _get(row, 10, "updated_at")

    qty = float(qty_raw) if qty_raw is not None else 0.0
    entry_price = float(entry_price_raw) if entry_price_raw is not None else 0.0
    atr = parse_optional_float(atr_raw)
    entry_time = _parse_iso(entry_time_raw)
    extreme_price = float(extreme_price_raw) if extreme_price_raw is not None else 0.0
    trailing_stop_price = parse_optional_float(trailing_stop_raw)
    trailing_stop_activated = bool(int(trailing_stop_activated_raw or 0))
    pending_exit = bool(int(pending_exit_raw or 0))
    updated_at = _parse_iso(updated_at_raw)

    return Position(
        symbol=str(symbol),
        side=str(side),
        qty=qty,
        entry_price=entry_price,
        entry_time=entry_time,
        extreme_price=extreme_price,
        atr=atr,
        trailing_stop_price=trailing_stop_price,
        trailing_stop_activated=trailing_stop_activated,
        pending_exit=pending_exit,
        updated_at=updated_at,
    )


def fill_from_row(row: Any) -> Fill:
    """Map DB fills row:
    (alpaca_order_id, client_order_id, symbol, side, delta_qty, cum_qty, cum_avg_price, timestamp_utc, fill_id, price_is_estimate, fill_dedupe_key, delta_fill_price)
    """
    alpaca_order_id = _get(row, 0, "alpaca_order_id")
    client_order_id = _get(row, 1, "client_order_id")
    symbol = _get(row, 2, "symbol")
    side = _get(row, 3, "side")
    delta_qty_raw = _get(row, 4, "delta_qty")
    cum_qty_raw = _get(row, 5, "cum_qty")
    cum_avg_price_raw = _get(row, 6, "cum_avg_price")
    timestamp_raw = _get(row, 7, "timestamp_utc")
    fill_id = _get(row, 8, "fill_id")
    price_is_estimate_raw = _get(row, 9, "price_is_estimate")
    delta_fill_price_raw = _get(row, 11, "delta_fill_price")

    delta_qty = float(delta_qty_raw) if delta_qty_raw is not None else 0.0
    cum_qty = float(cum_qty_raw) if cum_qty_raw is not None else 0.0
    cum_avg_price = parse_optional_float(cum_avg_price_raw)
    timestamp_utc = _parse_iso(timestamp_raw)
    if price_is_estimate_raw is None:
        # Align with DB default (1) and Fill dataclass default (True)
        price_is_estimate = True
    else:
        try:
            price_is_estimate = bool(int(price_is_estimate_raw))
        except (TypeError, ValueError):
            # On unparseable value, fall back to the same default
            price_is_estimate = True
    delta_fill_price = parse_optional_float(delta_fill_price_raw)

    return Fill(
        alpaca_order_id=str(alpaca_order_id) if alpaca_order_id is not None else "",
        client_order_id=str(client_order_id) if client_order_id is not None else "",
        symbol=str(symbol) if symbol is not None else "",
        side=str(side) if side is not None else "",
        delta_qty=delta_qty,
        cum_qty=cum_qty,
        cum_avg_price=cum_avg_price,
        timestamp_utc=timestamp_utc,
        fill_id=str(fill_id) if fill_id is not None else None,
        price_is_estimate=price_is_estimate,
        delta_fill_price=delta_fill_price,
    )


def exit_attempt_from_row(row: Any) -> ExitAttempt:
    symbol = _get(row, 0, "symbol") or ""
    attempt_count_raw = _get(row, 1, "attempt_count")
    last_attempt_raw = _get(row, 2, "last_attempt_ts_utc")
    reason = _get(row, 3, "reason")

    attempt_count = int(attempt_count_raw or 0)
    last_attempt_ts_utc = _parse_iso(last_attempt_raw)

    return ExitAttempt(
        symbol=str(symbol),
        attempt_count=attempt_count,
        last_attempt_ts_utc=last_attempt_ts_utc,
        reason=reason,
    )


def position_snapshot_from_row(row: Any) -> PositionSnapshot:
    """Map a positions_snapshot row into PositionSnapshot.

    Expected row shape: (symbol, qty, avg_entry_price) or similar mapping.
    """
    # Support tuple/list, sqlite3.Row, dict, or object
    symbol = _get(row, 0, "symbol") or ""
    qty_raw = _get(row, 1, "qty")
    avg_entry_raw = _get(row, 2, "avg_entry_price")
    # Timestamp may not be present in select; try index 3 or key 'timestamp_utc'
    timestamp_raw = _get(row, 3, "timestamp_utc") or _get(row, 0, "timestamp_utc")

    qty = float(qty_raw) if qty_raw is not None else 0.0
    avg_entry_price = float(avg_entry_raw) if avg_entry_raw is not None else 0.0
    timestamp = _parse_iso(timestamp_raw)

    return PositionSnapshot(
        symbol=str(symbol),
        qty=qty,
        avg_entry_price=avg_entry_price,
        timestamp_utc=timestamp,
    )
