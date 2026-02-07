"""Position tracker - track open positions with entry prices and trailing stops.

Tracks:
- entry_price, entry_time, highest_price per symbol
- trailing_stop_price, trailing_stop_activated per symbol
- Syncs with broker positions on startup
- Persists to SQLite via StateStore
"""

import logging
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from typing import Optional

from src.broker import Broker
from src.state_store import StateStore

logger = logging.getLogger(__name__)


@dataclass
class PositionData:
    """Data for a tracked position."""
    symbol: str
    side: str  # "long" or "short"
    qty: float
    entry_price: float
    entry_time: datetime
    highest_price: float  # For trailing stop calculation
    trailing_stop_price: Optional[float] = None
    trailing_stop_activated: bool = False


class PositionTrackerError(Exception):
    """Raised when position tracker operation fails."""
    pass


class PositionTracker:
    """Track positions with exit-related metadata."""
    
    def __init__(
        self,
        broker: Broker,
        state_store: StateStore,
        trailing_stop_enabled: bool = False,
        trailing_stop_activation_pct: float = 0.01,
        trailing_stop_trail_pct: float = 0.005,
    ) -> None:
        """Initialise position tracker.
        
        Args:
            broker: Broker client for position sync
            state_store: State store for persistence
            trailing_stop_enabled: Whether trailing stops are enabled
            trailing_stop_activation_pct: P&L % to activate trailing stop
            trailing_stop_trail_pct: Distance below highest price for trailing stop
        """
        self.broker = broker
        self.state_store = state_store
        self.trailing_stop_enabled = trailing_stop_enabled
        self.trailing_stop_activation_pct = trailing_stop_activation_pct
        self.trailing_stop_trail_pct = trailing_stop_trail_pct
        
        # In-memory position tracking: symbol -> PositionData
        self._positions: dict[str, PositionData] = {}
    
    def init_schema(self) -> None:
        """Create position tracking table if not exists."""
        import sqlite3
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS position_tracking (
                    symbol TEXT PRIMARY KEY,
                    side TEXT NOT NULL,
                    qty NUMERIC(10, 4) NOT NULL,
                    entry_price NUMERIC(10, 4) NOT NULL,
                    entry_time TEXT NOT NULL,
                    highest_price NUMERIC(10, 4) NOT NULL,
                    trailing_stop_price NUMERIC(10, 4),
                    trailing_stop_activated INTEGER DEFAULT 0,
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
    ) -> PositionData:
        """Start tracking a new position.
        
        Called on BUY fill.
        
        Args:
            symbol: Stock symbol
            fill_price: Entry price from fill
            qty: Position quantity
            side: Position side ("long" or "short")
        
        Returns:
            PositionData for the new position
        """
        now = datetime.now(timezone.utc)
        position = PositionData(
            symbol=symbol,
            side=side,
            qty=qty,
            entry_price=fill_price,
            entry_time=now,
            highest_price=fill_price,
            trailing_stop_price=None,
            trailing_stop_activated=False,
        )
        
        self._positions[symbol] = position
        self._persist_position(position)
        
        logger.info(
            f"Started tracking {symbol}: entry=${fill_price:.2f}, qty={qty}"
        )
        return position
    
    def stop_tracking(self, symbol: str) -> None:
        """Stop tracking a position.
        
        Called on SELL fill.
        
        Args:
            symbol: Stock symbol
        """
        if symbol in self._positions:
            del self._positions[symbol]
            self._remove_position(symbol)
            logger.info(f"Stopped tracking {symbol}")
    
    def get_position(self, symbol: str) -> Optional[PositionData]:
        """Get tracked position data for symbol.
        
        Args:
            symbol: Stock symbol
        
        Returns:
            PositionData or None if not tracked
        """
        return self._positions.get(symbol)
    
    def get_all_positions(self) -> list[PositionData]:
        """Get all tracked positions.
        
        Returns:
            List of PositionData
        """
        return list(self._positions.values())
    
    def update_current_price(self, symbol: str, current_price: float) -> Optional[PositionData]:
        """Update position with current price, handling trailing stop logic.
        
        Args:
            symbol: Stock symbol
            current_price: Current market price
        
        Returns:
            Updated PositionData or None if not tracked
        """
        position = self._positions.get(symbol)
        if not position:
            return None
        
        # Update highest price if current is higher
        if current_price > position.highest_price:
            position.highest_price = current_price
            
            # Update trailing stop if activated
            if self.trailing_stop_enabled and position.trailing_stop_activated:
                new_trailing_stop = current_price * (1 - self.trailing_stop_trail_pct)
                # Trailing stop only moves up, never down
                if position.trailing_stop_price is None or new_trailing_stop > position.trailing_stop_price:
                    position.trailing_stop_price = new_trailing_stop
                    logger.debug(
                        f"{symbol} trailing stop raised to ${position.trailing_stop_price:.2f}"
                    )
        
        # Check if trailing stop should be activated
        if self.trailing_stop_enabled and not position.trailing_stop_activated:
            unrealised_pct = (current_price - position.entry_price) / position.entry_price
            if unrealised_pct >= self.trailing_stop_activation_pct:
                position.trailing_stop_activated = True
                position.trailing_stop_price = current_price * (1 - self.trailing_stop_trail_pct)
                logger.info(
                    f"{symbol} trailing stop activated at ${position.trailing_stop_price:.2f} "
                    f"(current ${current_price:.2f}, P&L {unrealised_pct*100:.1f}%)"
                )
        
        self._persist_position(position)
        return position
    
    def calculate_pnl(self, symbol: str, current_price: float) -> tuple[float, float]:
        """Calculate unrealised P&L for position.
        
        Args:
            symbol: Stock symbol
            current_price: Current market price
        
        Returns:
            Tuple of (pnl_amount, pnl_pct)
        """
        position = self._positions.get(symbol)
        if not position:
            return 0.0, 0.0
        
        price_diff = current_price - position.entry_price
        pnl_amount = price_diff * position.qty
        pnl_pct = price_diff / position.entry_price
        
        return pnl_amount, pnl_pct
    
    async def sync_with_broker(self) -> dict:
        """Sync tracked positions with broker positions.
        
        - For each broker position not tracked: start tracking with broker's avg_entry_price
        - For each tracked position not at broker: stop tracking
        - Log warnings for mismatches
        
        Returns:
            Dict with sync results
        """
        logger.info("Syncing positions with broker...")
        
        try:
            broker_positions = self.broker.get_positions()
        except Exception as e:
            raise PositionTrackerError(f"Failed to fetch broker positions: {e}")
        
        broker_symbols = {p["symbol"] for p in broker_positions}
        tracked_symbols = set(self._positions.keys())
        
        # Positions to start tracking (at broker but not tracked)
        new_positions = []
        for pos in broker_positions:
            symbol = pos["symbol"]
            if symbol not in tracked_symbols:
                qty = float(pos["qty"])
                entry_price = float(pos["avg_entry_price"]) if pos["avg_entry_price"] else 0.0
                side = "long" if qty > 0 else "short"
                
                position = self.start_tracking(
                    symbol=symbol,
                    fill_price=entry_price,
                    qty=abs(qty),
                    side=side,
                )
                new_positions.append(symbol)
                logger.warning(
                    f"Position sync: started tracking {symbol} from broker "
                    f"(entry=${entry_price:.2f}, qty={qty})"
                )
        
        # Positions to stop tracking (tracked but not at broker)
        removed_positions = []
        for symbol in tracked_symbols:
            if symbol not in broker_symbols:
                self.stop_tracking(symbol)
                removed_positions.append(symbol)
                logger.warning(f"Position sync: stopped tracking {symbol} (not at broker)")
        
        # Check for quantity mismatches
        mismatches = []
        for pos in broker_positions:
            symbol = pos["symbol"]
            if symbol in self._positions:
                broker_qty = abs(float(pos["qty"]))
                tracked_qty = self._positions[symbol].qty
                if abs(broker_qty - tracked_qty) > 0.0001:  # Allow small floating point diff
                    mismatches.append({
                        "symbol": symbol,
                        "broker_qty": broker_qty,
                        "tracked_qty": tracked_qty,
                    })
                    logger.warning(
                        f"Position qty mismatch for {symbol}: "
                        f"broker={broker_qty}, tracked={tracked_qty}"
                    )
        
        logger.info(
            f"Position sync complete: {len(new_positions)} new, "
            f"{len(removed_positions)} removed, {len(mismatches)} mismatches"
        )
        
        return {
            "new_positions": new_positions,
            "removed_positions": removed_positions,
            "mismatches": mismatches,
            "total_tracked": len(self._positions),
        }
    
    def _persist_position(self, position: PositionData) -> None:
        """Persist position to SQLite."""
        import sqlite3
        now = datetime.now(timezone.utc).isoformat()
        
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT OR REPLACE INTO position_tracking
                (symbol, side, qty, entry_price, entry_time, highest_price,
                 trailing_stop_price, trailing_stop_activated, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                position.symbol,
                position.side,
                position.qty,
                position.entry_price,
                position.entry_time.isoformat(),
                position.highest_price,
                position.trailing_stop_price,
                1 if position.trailing_stop_activated else 0,
                now,
            ))
            conn.commit()
    
    def _remove_position(self, symbol: str) -> None:
        """Remove position from SQLite."""
        import sqlite3
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("DELETE FROM position_tracking WHERE symbol = ?", (symbol,))
            conn.commit()
    
    def load_persisted_positions(self) -> list[PositionData]:
        """Load positions from SQLite on startup.
        
        Returns:
            List of loaded PositionData
        """
        import sqlite3
        positions = []
        
        # Ensure table exists
        self.init_schema()
        
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("""
                SELECT symbol, side, qty, entry_price, entry_time, highest_price,
                       trailing_stop_price, trailing_stop_activated
                FROM position_tracking
            """)
            rows = cursor.fetchall()
            
            for row in rows:
                position = PositionData(
                    symbol=row[0],
                    side=row[1],
                    qty=row[2],
                    entry_price=row[3],
                    entry_time=datetime.fromisoformat(row[4]),
                    highest_price=row[5],
                    trailing_stop_price=row[6],
                    trailing_stop_activated=bool(row[7]),
                )
                self._positions[position.symbol] = position
                positions.append(position)
        
        logger.info(f"Loaded {len(positions)} persisted positions")
        return positions
