"""PositionTracker: track positions and provide utilities for exits.

This implementation supports both an in-memory tracked-position model used by
the `ExitManager` (rich `PositionData` objects, trailing-stop logic, persistence)
and a lightweight snapshot API useful for quick quantity lookups from the
`OrderManager`.

It intentionally keeps persistence methods minimal and synchronous to align
with the existing `StateStore` SQLite usage.
"""

import asyncio
import logging
import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Optional

from src.adapters.persistence.mappers import position_from_row
from src.async_broker_adapter import AsyncBrokerInterface
from src.state_store import StateStore

logger = logging.getLogger(__name__)


@dataclass
class PositionData:
    symbol: str
    side: str  # "long" or "short"
    qty: float
    entry_price: float
    entry_time: datetime
    extreme_price: float
    atr: Optional[float] = None
    trailing_stop_price: Optional[float] = None
    trailing_stop_activated: bool = False
    pending_exit: bool = False
    # Timestamp when this position snapshot was last persisted/updated
    last_updated: Optional[datetime] = None


class PositionTrackerError(Exception):
    pass


class PositionTracker:
    """Track positions with exit-related metadata and persistence."""

    def __init__(
        self,
        broker: AsyncBrokerInterface,
        state_store: StateStore,
        trailing_stop_enabled: bool = False,
        trailing_stop_activation_pct: float = 0.01,
        trailing_stop_trail_pct: float = 0.005,
    ) -> None:
        self.broker = broker
        self.state_store = state_store
        self.trailing_stop_enabled = trailing_stop_enabled
        self.trailing_stop_activation_pct = trailing_stop_activation_pct
        self.trailing_stop_trail_pct = trailing_stop_trail_pct

        # In-memory position tracking: symbol -> PositionData
        self._positions: Dict[str, PositionData] = {}
        # Per-symbol last-updated timestamps (keeps freshness per symbol)
        self._last_updates: Dict[str, datetime] = {}
        # Global last update for full-tracker operations
        self._last_update: Optional[datetime] = None
        # Store last closed position snapshots keyed by symbol so callers
        # can retrieve closed-position info immediately after a close
        # operation without changing the outward return semantics of
        # `update_position_from_fill` (which historically returned None
        # for closed positions).
        self._last_closed_snapshots: Dict[str, PositionData] = {}

    def start_tracking(
        self,
        symbol: str,
        fill_price: float,
        qty: float = 1.0,
        side: str = "long",
        atr: Optional[float] = None,
    ) -> PositionData:
        # Use a single timestamp for entry_time and last_updated to avoid
        # ordering ambiguities between creation and persistence.
        now_dt = datetime.now(timezone.utc)
        position = PositionData(
            symbol=symbol,
            side=side,
            qty=qty,
            entry_price=fill_price,
            entry_time=now_dt,
            extreme_price=fill_price,
            atr=atr,
            last_updated=now_dt,
        )
        self._positions[symbol] = position
        # Update per-symbol freshness before persistence and use the same
        # timestamp when writing the DB so the values remain consistent.
        self._last_updates[symbol] = now_dt
        self._last_update = now_dt
        # Call internal persist without forcing an extra timestamp arg so
        # test monkeypatches that replace `_persist_position(position)`
        # still work. `_persist_position` will prefer `position.last_updated`
        # if set above.
        self._persist_position(position)
        return position

    async def update_position_from_fill(
        self,
        symbol: str,
        delta_qty: float,
        fill_price: float,
        side: str,  # "buy" or "sell"
        timestamp: Optional[datetime] = None,
    ) -> Optional[PositionData]:
        """Update position based on a fill event.

        - Creates new position on first fill (not at order submission)
        - Updates existing position qty and blended avg price on subsequent fills
        - Closes position when qty reaches zero (full exit)

        Args:
            symbol: Trading symbol
            delta_qty: Incremental fill quantity (always positive)
            fill_price: Fill price for this incremental quantity
            side: "buy" (increases position) or "sell" (decreases position)
            timestamp: Optional timestamp for the fill

        Returns:
            Updated PositionData or None if position closed
        """
        if timestamp is None:
            timestamp = datetime.now(timezone.utc)

        # Skip zero-quantity fills
        if delta_qty <= 0:
            return self.get_position(symbol)

        position = self._positions.get(symbol)

        if side == "buy":
            if position is None:
                # First fill - create new long position
                position = PositionData(
                    symbol=symbol,
                    side="long",
                    qty=delta_qty,
                    entry_price=fill_price,
                    entry_time=timestamp,
                    extreme_price=fill_price,
                    last_updated=timestamp,
                )
                self._positions[symbol] = position
                self._last_updates[symbol] = timestamp
                self._last_update = timestamp
                self._persist_position(position, timestamp)
                logger.info(
                    "Long position created on first fill: %s qty=%.4f @ %.2f",
                    symbol,
                    delta_qty,
                    fill_price,
                )
            elif position.side == "short":
                # Closing/reducing short position
                current_qty = position.qty
                new_qty = current_qty - delta_qty

                if new_qty <= 0:
                    # Short position fully closed
                    closed_snapshot = PositionData(
                        symbol=position.symbol,
                        side=position.side,
                        qty=0.0,
                        entry_price=position.entry_price,
                        entry_time=position.entry_time,
                        extreme_price=position.extreme_price,
                        atr=position.atr,
                        trailing_stop_price=position.trailing_stop_price,
                        trailing_stop_activated=position.trailing_stop_activated,
                        pending_exit=position.pending_exit,
                        last_updated=timestamp,
                    )

                    # Store snapshot for immediate retrieval by callers
                    self._last_closed_snapshots[symbol] = closed_snapshot

                    # Remove in-memory and persisted state
                    self.stop_tracking(symbol)
                    logger.info(
                        "Short position closed on buy fill: %s qty=%.4f→0",
                        symbol,
                        current_qty,
                    )
                    return None
                else:
                    # Partial close - reduce short position
                    position.qty = new_qty
                    position.last_updated = timestamp
                    self._last_updates[symbol] = timestamp
                    self._last_update = timestamp
                    self._persist_position(position, timestamp)
                    logger.info(
                        "Short position decreased: %s qty=%.4f→%.4f",
                        symbol,
                        current_qty,
                        new_qty,
                    )
            else:
                # Increasing long position - blend average price
                current_qty = position.qty
                current_avg = position.entry_price
                new_qty = current_qty + delta_qty
                total_cost = (current_qty * current_avg) + (delta_qty * fill_price)
                new_avg = total_cost / new_qty

                position.qty = new_qty
                position.entry_price = new_avg
                position.last_updated = timestamp
                self._last_updates[symbol] = timestamp
                self._last_update = timestamp
                self._persist_position(position, timestamp)
                logger.info(
                    "Long position increased: %s qty=%.4f→%.4f avg=%.2f→%.2f",
                    symbol,
                    current_qty,
                    new_qty,
                    current_avg,
                    new_avg,
                )

        elif side == "sell":
            if position is None:
                # First sell fill with no existing position: treat as opening a short
                position = PositionData(
                    symbol=symbol,
                    side="short",
                    qty=delta_qty,
                    entry_price=fill_price,
                    entry_time=timestamp,
                    extreme_price=fill_price,
                    last_updated=timestamp,
                )
                self._positions[symbol] = position
                self._last_updates[symbol] = timestamp
                self._last_update = timestamp
                self._persist_position(position, timestamp)
                logger.info(
                    "Short position created on first sell fill: %s qty=%.4f @ %.2f",
                    symbol,
                    delta_qty,
                    fill_price,
                )
                return position

            # Decrease long position or increase short position
            if position.side == "short":
                # Increasing short position - weighted average entry price
                current_qty = position.qty
                current_entry = position.entry_price
                total_qty = current_qty + delta_qty

                # Weighted average: (qty1*price1 + qty2*price2) / (qty1 + qty2)
                new_entry_price = (
                    (current_qty * current_entry) + (delta_qty * fill_price)
                ) / total_qty

                position.qty = total_qty
                position.entry_price = new_entry_price
                position.last_updated = timestamp
                self._last_updates[symbol] = timestamp
                self._last_update = timestamp
                self._persist_position(position, timestamp)
                logger.info(
                    "Short position increased: %s qty=%.4f→%.4f entry=%.2f→%.2f",
                    symbol,
                    current_qty,
                    total_qty,
                    current_entry,
                    new_entry_price,
                )
                return position

            # Decrease long position - avg price unchanged
            current_qty = position.qty
            new_qty = current_qty - delta_qty

            if new_qty <= 0:
                # Position fully closed. Capture a snapshot of the position
                # before removing it so callers can access entry/exit info.
                closed_snapshot = PositionData(
                    symbol=position.symbol,
                    side=position.side,
                    qty=0.0,
                    entry_price=position.entry_price,
                    entry_time=position.entry_time,
                    extreme_price=position.extreme_price,
                    atr=position.atr,
                    trailing_stop_price=position.trailing_stop_price,
                    trailing_stop_activated=position.trailing_stop_activated,
                    pending_exit=position.pending_exit,
                    last_updated=timestamp,
                )

                # Store snapshot for immediate retrieval by callers (non-persistent)
                self._last_closed_snapshots[symbol] = closed_snapshot

                # Remove in-memory and persisted state
                self.stop_tracking(symbol)
                logger.info(
                    "Position closed on sell fill: %s qty=%.4f→0",
                    symbol,
                    current_qty,
                )
                # Preserve original API: return None to indicate the position
                # is closed (as callers historically expected).
                return None
            else:
                position.qty = new_qty
                position.last_updated = timestamp
                self._last_updates[symbol] = timestamp
                self._last_update = timestamp
                self._persist_position(position, timestamp)
                logger.info(
                    "Position decreased: %s qty=%.4f→%.4f",
                    symbol,
                    current_qty,
                    new_qty,
                )

        return position

    def stop_tracking(self, symbol: str) -> None:
        if symbol in self._positions:
            del self._positions[symbol]
            self._remove_position(symbol)

    def get_position(self, symbol: str) -> Optional[PositionData]:
        return self._positions.get(symbol)

    def get_all_positions(self) -> List[PositionData]:
        return list(self._positions.values())

    def get_position_qty(self, symbol: str) -> Optional[float]:
        p = self.get_position(symbol)
        return p.qty if p is not None else None

    def pop_last_closed_snapshot(self, symbol: str) -> Optional[PositionData]:
        """Return and remove the last closed position snapshot for `symbol`.

        This provides a safe handoff for callers that need to access the
        closed position's entry/exit info immediately after a close.
        """
        return self._last_closed_snapshots.pop(symbol, None)

    def update_current_price(self, symbol: str, current_price: float) -> Optional[PositionData]:
        position = self._positions.get(symbol)
        if not position:
            return None

        state_changed = False
        converted_side = (position.side or "").lower()

        if converted_side == "long":
            if current_price > position.extreme_price:
                position.extreme_price = current_price
                state_changed = True
                if self.trailing_stop_enabled and position.trailing_stop_activated:
                    new_trailing_stop = current_price * (1 - self.trailing_stop_trail_pct)
                    if (
                        position.trailing_stop_price is None
                        or new_trailing_stop > position.trailing_stop_price
                    ):
                        position.trailing_stop_price = new_trailing_stop
                        state_changed = True
            if self.trailing_stop_enabled and not position.trailing_stop_activated:
                unrealised_pct = (current_price - position.entry_price) / position.entry_price
                if unrealised_pct >= self.trailing_stop_activation_pct:
                    position.trailing_stop_activated = True
                    position.trailing_stop_price = current_price * (
                        1 - self.trailing_stop_trail_pct
                    )
                    state_changed = True

        elif converted_side == "short":
            if current_price < position.extreme_price:
                position.extreme_price = current_price
                state_changed = True
                if self.trailing_stop_enabled and position.trailing_stop_activated:
                    new_trailing_stop = current_price * (1 + self.trailing_stop_trail_pct)
                    if (
                        position.trailing_stop_price is None
                        or new_trailing_stop < position.trailing_stop_price
                    ):
                        position.trailing_stop_price = new_trailing_stop
                        state_changed = True
            if self.trailing_stop_enabled and not position.trailing_stop_activated:
                unrealised_pct = (position.entry_price - current_price) / position.entry_price
                if unrealised_pct >= self.trailing_stop_activation_pct:
                    position.trailing_stop_activated = True
                    position.trailing_stop_price = current_price * (
                        1 + self.trailing_stop_trail_pct
                    )
                    state_changed = True

        else:
            logger.warning("Unsupported position side '%s' for %s", position.side, symbol)
            return position

        if state_changed:
            self._persist_position(position)

        return position

    def calculate_pnl(self, symbol: str, current_price: float) -> tuple[float, float]:
        position = self._positions.get(symbol)
        if not position:
            return 0.0, 0.0
        if position.entry_price <= 0:
            return 0.0, 0.0
        if position.side == "long":
            price_diff = current_price - position.entry_price
        elif position.side == "short":
            price_diff = position.entry_price - current_price
        else:
            logger.warning(
                "Unsupported position side '%s' for P&L calc on symbol %s; returning 0.0",
                position.side,
                symbol,
            )
            return 0.0, 0.0
        pnl_amount = price_diff * position.qty
        pnl_pct = price_diff / position.entry_price
        return pnl_amount, pnl_pct

    async def sync_with_broker(self) -> dict[str, object]:
        try:
            maybe = self.broker.get_positions()
            broker_positions = await maybe if asyncio.iscoroutine(maybe) else maybe
        except Exception as e:
            raise PositionTrackerError(f"Failed to fetch broker positions: {e}")

        broker_symbols = {p["symbol"] for p in broker_positions}
        tracked_symbols = set(self._positions.keys())

        new_positions: List[str] = []
        for pos in broker_positions:
            symbol = pos["symbol"]
            if symbol not in tracked_symbols:
                qty = abs(float(pos.get("qty", 0) or 0))
                entry_price = float(pos.get("avg_entry_price") or 0.0)
                side = "long" if float(pos.get("qty", 0) or 0) > 0 else "short"
                # If the broker returns a zero-quantity position record, avoid
                # tracking it. Starting tracking for qty == 0 can create
                # phantom positions (and the > 0 check above will classify
                # zero as "short"). Skip near-zero quantities to prevent
                # persisting and acting on meaningless positions.
                if qty == 0:
                    continue

                self.start_tracking(symbol=symbol, fill_price=entry_price, qty=qty, side=side)
                new_positions.append(symbol)

        removed_positions: List[str] = []
        for symbol in tracked_symbols:
            if symbol not in broker_symbols:
                self.stop_tracking(symbol)
                removed_positions.append(symbol)

        mismatches: List[dict[str, object]] = []
        for pos in broker_positions:
            symbol = pos["symbol"]
            if symbol in self._positions:
                broker_qty = abs(float(pos.get("qty", 0) or 0))
                tracked_qty = self._positions[symbol].qty
                if abs(broker_qty - tracked_qty) > 0.0001:
                    mismatches.append(
                        {"symbol": symbol, "broker_qty": broker_qty, "tracked_qty": tracked_qty}
                    )

        return {
            "new_positions": new_positions,
            "removed_positions": removed_positions,
            "mismatches": mismatches,
            "total_tracked": len(self._positions),
        }

    def _persist_position(self, position: PositionData, now_dt: Optional[datetime] = None) -> None:
        # Determine the timestamp we'll use for persistence. Prefer an
        # externally-provided `now_dt`, otherwise use `position.last_updated`
        # if it was set by the caller (so callers can provide a single
        # timestamp), else compute a fresh timestamp.
        if now_dt is not None:
            used_dt = now_dt
        elif position.last_updated is not None:
            used_dt = position.last_updated
        else:
            used_dt = datetime.now(timezone.utc)
        now = used_dt.isoformat()
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                """
                INSERT OR REPLACE INTO position_tracking
                (symbol, side, qty, entry_price, atr, entry_time, extreme_price,
                 trailing_stop_price, trailing_stop_activated, pending_exit, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    position.symbol,
                    position.side,
                    position.qty,
                    position.entry_price,
                    position.atr,
                    position.entry_time.isoformat(),
                    position.extreme_price,
                    position.trailing_stop_price,
                    1 if position.trailing_stop_activated else 0,
                    1 if position.pending_exit else 0,
                    now,
                ),
            )
            conn.commit()
        # mark last update timestamp after successful commit (idempotent)
        position.last_updated = used_dt
        self._last_updates[position.symbol] = used_dt
        self._last_update = used_dt

    # Public persistence API
    def persist_position(self, position: PositionData, now_dt: Optional[datetime] = None) -> None:
        """Persist a position (public wrapper)."""
        return self._persist_position(position, now_dt)

    def upsert_position(self, position: PositionData) -> None:
        """Public API to upsert a position: update in-memory state and persist.

        This method centralises the logic for keeping the in-memory tracker and
        the persistent store in sync. Callers should use this instead of
        reaching into private persistence helpers.
        """
        now_dt = datetime.now(timezone.utc)
        # Update in-memory structures
        self._positions[position.symbol] = position
        position.last_updated = now_dt
        self._last_updates[position.symbol] = now_dt
        self._last_update = now_dt
        # Persist the authoritative snapshot
        self._persist_position(position, now_dt)

    def _remove_position(self, symbol: str) -> None:
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("DELETE FROM position_tracking WHERE symbol = ?", (symbol,))
            conn.commit()

    def remove_position(self, symbol: str) -> None:
        """Remove persisted position (public wrapper)."""
        return self._remove_position(symbol)

    def load_persisted_positions(self) -> List[PositionData]:
        positions: List[PositionData] = []
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("""
                SELECT symbol, side, qty, entry_price, atr, entry_time, extreme_price,
                       trailing_stop_price, trailing_stop_activated, COALESCE(pending_exit, 0), updated_at
                FROM position_tracking
                """)
            rows = cursor.fetchall()
            for row in rows:
                # Map DB row to persistence Position dataclass then convert to PositionData
                p = position_from_row(row)

                position = PositionData(
                    symbol=p.symbol,
                    side=p.side,
                    qty=p.qty,
                    entry_price=p.entry_price,
                    entry_time=p.entry_time or datetime.now(timezone.utc),
                    extreme_price=p.extreme_price,
                    atr=p.atr,
                    trailing_stop_price=p.trailing_stop_price,
                    trailing_stop_activated=bool(p.trailing_stop_activated),
                    pending_exit=bool(p.pending_exit),
                    last_updated=p.updated_at,
                )

                if p.updated_at:
                    self._last_updates[position.symbol] = p.updated_at

                self._positions[position.symbol] = position
                positions.append(position)

        return positions

    def last_updated(self, symbol: Optional[str] = None) -> Optional[datetime]:
        """Return per-symbol last-updated timestamp if `symbol` provided,
        otherwise return the global last-updated timestamp for the tracker.

        This allows callers to assess freshness for a specific symbol.
        """
        if symbol is None:
            return self._last_update
        return self._last_updates.get(symbol)
