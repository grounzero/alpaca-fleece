"""SQLite state store - schema and basic queries.

Provides a crash-safe persistence layer for order intents, trades, equity curve,
and bot state. All financial values use NUMERIC for precision.
"""

import logging
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional, TypedDict


from src.utils import parse_optional_float

logger = logging.getLogger(__name__)


# Use the public utility from src.utils for DB numeric coercion directly.


class OrderIntentRow(TypedDict, total=False):
    """Order intent row from database."""

    client_order_id: str
    symbol: str
    side: str
    qty: float
    atr: Optional[float]
    status: str
    filled_qty: Optional[float]
    alpaca_order_id: Optional[str]


class StateStoreError(Exception):
    """Raised when state store operation fails."""

    pass


class StateStore:
    """SQLite state persistence."""

    def __init__(self, db_path: str) -> None:
        """Initialise state store.

        Args:
            db_path: Path to SQLite database
        """
        self.db_path = db_path
        Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)
        self.init_schema()

    def init_schema(self) -> None:
        """Create tables if they don't exist.

        Uses NUMERIC(10,4) for financial values (qty, price) to avoid
        floating-point precision errors common with REAL.
        """
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()

            # Order intents table
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS order_intents (
                    client_order_id TEXT PRIMARY KEY,
                    symbol TEXT NOT NULL,
                    side TEXT NOT NULL,
                    qty NUMERIC(10, 4) NOT NULL,
                    atr NUMERIC(10, 4),
                    status TEXT NOT NULL,
                    filled_qty NUMERIC(10, 4) DEFAULT 0,
                    alpaca_order_id TEXT,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                )
            """)

            # Trades table
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS trades (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    side TEXT NOT NULL,
                    qty NUMERIC(10, 4) NOT NULL,
                    price NUMERIC(10, 4) NOT NULL,
                    order_id TEXT NOT NULL,
                    client_order_id TEXT NOT NULL
                )
            """)

            # Equity curve table
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS equity_curve (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    equity NUMERIC(12, 2) NOT NULL,
                    daily_pnl NUMERIC(12, 2) NOT NULL
                )
            """)

            # Bot state table (key-value)
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS bot_state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                )
            """)

            # Bars table
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS bars (
                    symbol TEXT NOT NULL,
                    timeframe TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    open NUMERIC(10, 4), high NUMERIC(10, 4), low NUMERIC(10, 4), close NUMERIC(10, 4),
                    volume INTEGER, trade_count INTEGER, vwap NUMERIC(10, 4),
                    PRIMARY KEY (symbol, timeframe, timestamp_utc)
                )
            """)

            # Positions snapshot table
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS positions_snapshot (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    qty NUMERIC(10, 4) NOT NULL,
                    avg_entry_price NUMERIC(10, 4) NOT NULL
                )
            """)

            # Create indexes for frequently queried columns
            cursor.execute("""
                CREATE INDEX IF NOT EXISTS idx_order_intents_status 
                ON order_intents(status)
            """)

            cursor.execute("""
                CREATE INDEX IF NOT EXISTS idx_order_intents_symbol 
                ON order_intents(symbol)
            """)

            cursor.execute("""
                CREATE INDEX IF NOT EXISTS idx_trades_symbol_timestamp 
                ON trades(symbol, timestamp_utc)
            """)

            cursor.execute("""
                CREATE INDEX IF NOT EXISTS idx_bars_symbol_timestamp 
                ON bars(symbol, timestamp_utc)
            """)

            cursor.execute("""
                CREATE INDEX IF NOT EXISTS idx_positions_snapshot_timestamp 
                ON positions_snapshot(timestamp_utc)
            """)

            cursor.execute("""
                CREATE INDEX IF NOT EXISTS idx_equity_curve_timestamp 
                ON equity_curve(timestamp_utc)
            """)

            conn.commit()

            # Migration: ensure `atr` column exists on order_intents for older DBs
            try:
                cursor.execute("PRAGMA table_info(order_intents)")
                oi_columns = [col[1] for col in cursor.fetchall()]
                if "atr" not in oi_columns:
                    cursor.execute("ALTER TABLE order_intents ADD COLUMN atr NUMERIC(10, 4)")
                    conn.commit()
            except sqlite3.Error as e:
                # Use the module-level logger (defined at top of this module)
                # rather than creating a new logger instance here. This keeps
                # logging consistent and is safe during early init/migration.
                logger.warning("Could not migrate order_intents to add atr column: %s", e)

    def get_state(self, key: str) -> Optional[str]:
        """Get state value by key."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT value FROM bot_state WHERE key = ?", (key,))
            row = cursor.fetchone()
            return row[0] if row else None

    def set_state(self, key: str, value: str) -> None:
        """Set state value."""
        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "INSERT OR REPLACE INTO bot_state (key, value, updated_at_utc) VALUES (?, ?, ?)",
                (key, value, now),
            )
            conn.commit()

    def save_order_intent(
        self,
        client_order_id: str,
        symbol: str,
        side: str,
        qty: float,
        status: str = "new",
        atr: float | None = None,
    ) -> None:
        """Save order intent before submission (crash safety)."""
        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                """INSERT INTO order_intents 
                   (client_order_id, symbol, side, qty, atr, status, created_at_utc, updated_at_utc)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                (client_order_id, symbol, side, qty, atr, status, now, now),
            )
            conn.commit()

    def update_order_intent(
        self,
        client_order_id: str,
        status: str,
        filled_qty: float,
        alpaca_order_id: Optional[str] = None,
    ) -> None:
        """Update order intent after status change."""
        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                """UPDATE order_intents 
                   SET status = ?, filled_qty = ?, alpaca_order_id = ?, updated_at_utc = ?
                   WHERE client_order_id = ?""",
                (status, filled_qty, alpaca_order_id, now, client_order_id),
            )
            conn.commit()

    def get_order_intent(self, client_order_id: str) -> Optional[dict[str, object]]:
        """Get order intent by client_order_id."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT client_order_id, symbol, side, qty, atr, status, filled_qty, alpaca_order_id FROM order_intents WHERE client_order_id = ?",
                (client_order_id,),
            )
            row = cursor.fetchone()
            if row:
                atr_val = parse_optional_float(row[4])
                # Convert required numeric fields to float and optional ones
                # via the shared helper to avoid leaking Decimal/str values.
                return {
                    "client_order_id": row[0],
                    "symbol": row[1],
                    "side": row[2],
                    "qty": float(row[3]),
                    "atr": atr_val,
                    "status": row[5],
                    "filled_qty": parse_optional_float(row[6]),
                    "alpaca_order_id": row[7],
                }
            return None

    def get_all_order_intents(self, status: Optional[str] = None) -> list[OrderIntentRow]:
        """Get all order intents, optionally filtered by status.

        Args:
            status: Optional status filter (e.g., 'new', 'submitted', 'filled')

        Returns:
            List of order intent rows
        """
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            if status:
                cursor.execute(
                    "SELECT client_order_id, symbol, side, qty, atr, status, filled_qty, alpaca_order_id FROM order_intents WHERE status = ?",
                    (status,),
                )
            else:
                cursor.execute(
                    "SELECT client_order_id, symbol, side, qty, atr, status, filled_qty, alpaca_order_id FROM order_intents"
                )

            rows = cursor.fetchall()

            def map_row(row: tuple[Any, ...]) -> OrderIntentRow:
                atr_val = parse_optional_float(row[4])
                return {
                    "client_order_id": row[0],
                    "symbol": row[1],
                    "side": row[2],
                    "qty": float(row[3]),
                    "atr": atr_val,
                    "status": row[5],
                    "filled_qty": parse_optional_float(row[6]),
                    "alpaca_order_id": row[7],
                }

            return [map_row(row) for row in rows]

    # Win #3: Daily Limits & Circuit Breaker Persistence

    def save_circuit_breaker_count(self, count: int) -> None:
        """Persist circuit breaker failure count (Win #3)."""
        self.set_state("circuit_breaker_count", str(count))

    def get_circuit_breaker_count(self) -> int:
        """Load circuit breaker failure count from DB (Win #3)."""
        value = self.get_state("circuit_breaker_count")
        return int(value) if value else 0

    def save_daily_pnl(self, pnl: float) -> None:
        """Persist daily P&L across restarts (Win #3)."""
        self.set_state("daily_pnl", str(pnl))

    def get_daily_pnl(self) -> float:
        """Load daily P&L from DB (Win #3)."""
        value = self.get_state("daily_pnl")
        return float(value) if value else 0.0

    def save_daily_trade_count(self, count: int) -> None:
        """Persist daily trade count across restarts (Win #3)."""
        self.set_state("daily_trade_count", str(count))

    def get_daily_trade_count(self) -> int:
        """Load daily trade count from DB (Win #3)."""
        value = self.get_state("daily_trade_count")
        return int(value) if value else 0

    def save_last_signal(
        self, symbol: str, signal_type: str, sma_period: tuple[int, int] = (10, 30)
    ) -> None:
        """Persist last signal per symbol per SMA period (Win #3).

        Args:
            symbol: Stock symbol (e.g., 'AAPL')
            signal_type: 'BUY' or 'SELL'
            sma_period: (fast, slow) tuple
        """
        key = f"last_signal:{symbol}:{sma_period[0]}:{sma_period[1]}"
        self.set_state(key, signal_type)

    def get_last_signal(self, symbol: str, sma_period: tuple[int, int] = (10, 30)) -> Optional[str]:
        """Load last signal per symbol per SMA period (Win #3)."""
        key = f"last_signal:{symbol}:{sma_period[0]}:{sma_period[1]}"
        return self.get_state(key)

    def reset_daily_state(self) -> None:
        """Reset daily limits at market open (Win #3)."""
        self.save_daily_pnl(0.0)
        self.save_daily_trade_count(0)
