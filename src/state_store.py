"""SQLite persistence for bot state."""
import sqlite3
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional
import json


class StateStore:
    """Manage persistent state in SQLite."""

    def __init__(self, db_path: Path = None):
        """
        Set up state store.

        Args:
            db_path: Path to SQLite database file (default: data/bot_state.db)
        """
        if db_path is None:
            db_path = Path(__file__).parent.parent / "data" / "bot_state.db"

        db_path.parent.mkdir(exist_ok=True)
        self.db_path = db_path
        self.conn = sqlite3.connect(str(db_path), check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        self._init_schema()

    def _init_schema(self):
        """Create tables if they don't exist."""
        cursor = self.conn.cursor()

        # Order intents table
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS order_intents (
                client_order_id TEXT PRIMARY KEY,
                symbol TEXT NOT NULL,
                side TEXT NOT NULL,
                qty REAL NOT NULL,
                status TEXT NOT NULL,
                filled_qty REAL DEFAULT 0,
                alpaca_order_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
        """)

        # Trades table
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS trades (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                symbol TEXT NOT NULL,
                side TEXT NOT NULL,
                qty REAL NOT NULL,
                price REAL NOT NULL,
                order_id TEXT,
                client_order_id TEXT NOT NULL,
                FOREIGN KEY (client_order_id) REFERENCES order_intents(client_order_id)
            )
        """)

        # Equity curve table
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS equity_curve (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                equity REAL NOT NULL,
                daily_pnl REAL
            )
        """)

        # Bot state table (key-value store)
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS bot_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
        """)

        # Create indices
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_trades_timestamp ON trades(timestamp)")
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_trades_symbol ON trades(symbol)")
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_equity_timestamp ON equity_curve(timestamp)")
        cursor.execute("CREATE INDEX IF NOT EXISTS idx_order_intents_symbol ON order_intents(symbol)")

        self.conn.commit()

    def save_order_intent(
        self,
        client_order_id: str,
        symbol: str,
        side: str,
        qty: float,
        status: str = "pending",
        alpaca_order_id: Optional[str] = None,
    ):
        """Save order intent to database."""
        cursor = self.conn.cursor()
        now = datetime.utcnow().isoformat()

        cursor.execute("""
            INSERT OR REPLACE INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        """, (client_order_id, symbol, side, qty, status, alpaca_order_id, now, now))

        self.conn.commit()

    def update_order_status(
        self,
        client_order_id: str,
        status: str,
        filled_qty: Optional[float] = None,
        alpaca_order_id: Optional[str] = None,
    ):
        """Update order status."""
        cursor = self.conn.cursor()
        now = datetime.utcnow().isoformat()

        if filled_qty is not None:
            cursor.execute("""
                UPDATE order_intents
                SET status = ?, filled_qty = ?, alpaca_order_id = COALESCE(?, alpaca_order_id), updated_at = ?
                WHERE client_order_id = ?
            """, (status, filled_qty, alpaca_order_id, now, client_order_id))
        else:
            cursor.execute("""
                UPDATE order_intents
                SET status = ?, alpaca_order_id = COALESCE(?, alpaca_order_id), updated_at = ?
                WHERE client_order_id = ?
            """, (status, alpaca_order_id, now, client_order_id))

        self.conn.commit()

    def get_order_intent(self, client_order_id: str) -> Optional[Dict[str, Any]]:
        """Get order intent by client_order_id."""
        cursor = self.conn.cursor()
        cursor.execute("SELECT * FROM order_intents WHERE client_order_id = ?", (client_order_id,))
        row = cursor.fetchone()
        return dict(row) if row else None

    def order_exists(self, client_order_id: str) -> bool:
        """Check if order intent exists."""
        return self.get_order_intent(client_order_id) is not None

    def save_trade(
        self,
        symbol: str,
        side: str,
        qty: float,
        price: float,
        client_order_id: str,
        order_id: Optional[str] = None,
    ):
        """Save trade to database."""
        cursor = self.conn.cursor()
        now = datetime.utcnow().isoformat()

        cursor.execute("""
            INSERT INTO trades (timestamp, symbol, side, qty, price, order_id, client_order_id)
            VALUES (?, ?, ?, ?, ?, ?, ?)
        """, (now, symbol, side, qty, price, order_id, client_order_id))

        self.conn.commit()

    def save_equity(self, equity: float, daily_pnl: Optional[float] = None):
        """Save equity snapshot."""
        cursor = self.conn.cursor()
        now = datetime.utcnow().isoformat()

        cursor.execute("""
            INSERT INTO equity_curve (timestamp, equity, daily_pnl)
            VALUES (?, ?, ?)
        """, (now, equity, daily_pnl))

        self.conn.commit()

    def get_state(self, key: str) -> Optional[str]:
        """Get state value by key."""
        cursor = self.conn.cursor()
        cursor.execute("SELECT value FROM bot_state WHERE key = ?", (key,))
        row = cursor.fetchone()
        return row["value"] if row else None

    def set_state(self, key: str, value: Any):
        """Set state value (stored as JSON string)."""
        cursor = self.conn.cursor()
        now = datetime.utcnow().isoformat()

        # Convert value to JSON string
        value_str = json.dumps(value) if not isinstance(value, str) else value

        cursor.execute("""
            INSERT OR REPLACE INTO bot_state (key, value, updated_at)
            VALUES (?, ?, ?)
        """, (key, value_str, now))

        self.conn.commit()

    def delete_state(self, key: str):
        """Delete state by key."""
        cursor = self.conn.cursor()
        cursor.execute("DELETE FROM bot_state WHERE key = ?", (key,))
        self.conn.commit()

    def get_daily_trade_count(self) -> int:
        """Get daily trade count."""
        value = self.get_state("daily_trade_count")
        return int(value) if value else 0

    def increment_daily_trade_count(self):
        """Increment daily trade count."""
        count = self.get_daily_trade_count()
        self.set_state("daily_trade_count", count + 1)

    def reset_daily_trade_count(self):
        """Reset daily trade count to zero."""
        self.set_state("daily_trade_count", 0)

    def get_circuit_breaker_state(self) -> Dict[str, Any]:
        """Get circuit breaker state."""
        state = self.get_state("circuit_breaker_state")
        if state:
            return json.loads(state)
        return {"tripped": False, "failures": 0}

    def set_circuit_breaker_state(self, tripped: bool, failures: int):
        """Set circuit breaker state."""
        self.set_state("circuit_breaker_state", {
            "tripped": tripped,
            "failures": failures,
        })

    def reset_circuit_breaker(self):
        """Reset circuit breaker."""
        self.set_circuit_breaker_state(tripped=False, failures=0)

    def get_last_signal(self, symbol: str) -> Optional[str]:
        """Get last signal for symbol (BUY, SELL, or None)."""
        return self.get_state(f"last_signal:{symbol}")

    def set_last_signal(self, symbol: str, signal: str):
        """Set last signal for symbol."""
        self.set_state(f"last_signal:{symbol}", signal)

    def get_recent_trades(self, limit: int = 100) -> List[Dict[str, Any]]:
        """Get recent trades."""
        cursor = self.conn.cursor()
        cursor.execute("""
            SELECT * FROM trades
            ORDER BY timestamp DESC
            LIMIT ?
        """, (limit,))
        return [dict(row) for row in cursor.fetchall()]

    def close(self):
        """Close database connection."""
        self.conn.close()
