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

from src.broker import Broker
from src.state_store import StateStore
from src.utils import parse_optional_float

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


class PositionTrackerError(Exception):
    pass


class PositionTracker:
    """Track positions with exit-related metadata and persistence."""

    def __init__(
        self,
        broker: Broker,
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
        self._last_update: Optional[datetime] = None

    def init_schema(self) -> None:
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS position_tracking (
                    symbol TEXT PRIMARY KEY,
                    side TEXT NOT NULL,
                    qty NUMERIC(10, 4) NOT NULL,
                    entry_price NUMERIC(10, 4) NOT NULL,
                    atr NUMERIC(10, 4),
                    entry_time TEXT NOT NULL,
                    extreme_price NUMERIC(10,4) NOT NULL,
                    trailing_stop_price NUMERIC(10, 4),
                    trailing_stop_activated INTEGER DEFAULT 0,
                    pending_exit INTEGER DEFAULT 0,
                    updated_at TEXT NOT NULL
                )
                """)
            conn.commit()

    def start_tracking(
        self,
        symbol: str,
        fill_price: float,
        qty: float = 1.0,
        side: str = "long",
        atr: Optional[float] = None,
    ) -> PositionData:
        now = datetime.now(timezone.utc)
        position = PositionData(
            symbol=symbol,
            side=side,
            qty=qty,
            entry_price=fill_price,
            entry_time=now,
            extreme_price=fill_price,
            atr=atr,
        )
        self._positions[symbol] = position
        self._persist_position(position)
        # mark last update time when we modify snapshot
        self._last_update = datetime.now(timezone.utc)
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
            broker_positions = await asyncio.to_thread(self.broker.get_positions)
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

    def _persist_position(self, position: PositionData) -> None:
        now = datetime.now(timezone.utc).isoformat()
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
        # mark last update timestamp after successful commit
        self._last_update = datetime.now(timezone.utc)

    # Public persistence API
    def persist_position(self, position: PositionData) -> None:
        """Persist a position (public wrapper)."""
        return self._persist_position(position)

    def _remove_position(self, symbol: str) -> None:
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("DELETE FROM position_tracking WHERE symbol = ?", (symbol,))
            conn.commit()

    def remove_position(self, symbol: str) -> None:
        """Remove persisted position (public wrapper)."""
        return self._remove_position(symbol)

    def load_persisted_positions(self) -> List[PositionData]:
        self.init_schema()
        positions: List[PositionData] = []
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("""
                SELECT symbol, side, qty, entry_price, atr, entry_time, extreme_price,
                       trailing_stop_price, trailing_stop_activated, COALESCE(pending_exit, 0)
                FROM position_tracking
                """)
            rows = cursor.fetchall()
            for row in rows:
                atr_val = parse_optional_float(row[4])
                trailing_stop_val = parse_optional_float(row[7])
                position = PositionData(
                    symbol=row[0],
                    side=row[1],
                    qty=float(row[2]),
                    entry_price=float(row[3]),
                    atr=atr_val,
                    entry_time=datetime.fromisoformat(row[5]),
                    extreme_price=float(row[6]),
                    trailing_stop_price=trailing_stop_val,
                    trailing_stop_activated=bool(int(row[8] or 0)),
                    pending_exit=bool(int(row[9] or 0)),
                )
                self._positions[position.symbol] = position
                positions.append(position)

        return positions

    def last_updated(self) -> Optional[datetime]:
        """Return timestamp when snapshot was last updated (or None)."""
        return self._last_update
