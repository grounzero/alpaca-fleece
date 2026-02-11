"""SQLite state store - schema and basic queries.

Provides a crash-safe persistence layer for order intents, trades, equity curve,
and bot state. All financial values use NUMERIC for precision.
"""

import logging
import sqlite3
from datetime import datetime, timedelta, timezone
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
    filled_avg_price: Optional[float]
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
                    filled_avg_price NUMERIC(10, 4),
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
                    client_order_id TEXT NOT NULL,
                    fill_id TEXT,
                    UNIQUE (order_id, fill_id),
                    UNIQUE (order_id, client_order_id)
                )
            """)

            # Ensure uniqueness to avoid duplicate insertions coming from
            # streaming + polling or reconnection replays. We add two unique
            # constraints so that either a (order_id, fill_id) pair (when
            # fills provide an id) or (order_id, client_order_id) act as a
            # dedupe key. SQLite can't alter table constraints easily, so
            # perform a safe migration if the existing table lacks these
            # constraints/columns.
            try:
                cursor.execute("PRAGMA table_info(trades)")
                trades_columns = [col[1] for col in cursor.fetchall()]
                need_migration = False
                # If fill_id missing or unique indexes not present, migrate
                if "fill_id" not in trades_columns:
                    need_migration = True
                else:
                    # Check for unique indexes on desired columns
                    cursor.execute("PRAGMA index_list(trades)")
                    indexes = cursor.fetchall()
                    # Look for indexes that are unique and cover (order_id,fill_id)
                    has_unique_order_fill = False
                    has_unique_order_client = False
                    for idx in indexes:
                        # idx: (seq, name, unique, origin, partial)
                        idx_name = idx[1]
                        is_unique = idx[2]
                        if not is_unique:
                            continue

                        # Safely quote the index name as an identifier for the PRAGMA call
                        def _quote_identifier(name: str) -> str:
                            return '"' + name.replace('"', '""') + '"'

                        quoted_idx_name = _quote_identifier(idx_name)
                        cursor.execute(f"PRAGMA index_info({quoted_idx_name})")
                        idx_cols = [c[2] for c in cursor.fetchall()]
                        if idx_cols == ["order_id", "fill_id"]:
                            has_unique_order_fill = True
                        if idx_cols == ["order_id", "client_order_id"]:
                            has_unique_order_client = True
                    if not (has_unique_order_fill and has_unique_order_client):
                        need_migration = True

                if need_migration:
                    # Create a new table with the desired schema and unique constraints
                    # Ensure any leftover `trades_new` from a prior partial
                    # migration is removed so the migration is deterministic.
                    cursor.execute("DROP TABLE IF EXISTS trades_new")

                    cursor.execute("""
                        CREATE TABLE trades_new (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            timestamp_utc TEXT NOT NULL,
                            symbol TEXT NOT NULL,
                            side TEXT NOT NULL,
                            qty NUMERIC(10, 4) NOT NULL,
                            price NUMERIC(10, 4) NOT NULL,
                            order_id TEXT NOT NULL,
                            client_order_id TEXT NOT NULL,
                            fill_id TEXT,
                            UNIQUE(order_id, fill_id),
                            UNIQUE(order_id, client_order_id)
                        )
                    """)

                    # Copy existing rows into new table keeping first-seen row
                    # for any duplicate dedupe key. Use INSERT OR IGNORE to let
                    # the UNIQUE constraints drop duplicates during copy.
                    # If the existing `trades` table already has a `fill_id`
                    # column, preserve its values; otherwise insert NULL.
                    if "fill_id" in trades_columns:
                        cursor.execute(
                            "INSERT OR IGNORE INTO trades_new (timestamp_utc, symbol, side, qty, price, order_id, client_order_id, fill_id) SELECT timestamp_utc, symbol, side, qty, price, order_id, client_order_id, fill_id FROM trades ORDER BY id ASC"
                        )
                    else:
                        cursor.execute(
                            "INSERT OR IGNORE INTO trades_new (timestamp_utc, symbol, side, qty, price, order_id, client_order_id, fill_id) SELECT timestamp_utc, symbol, side, qty, price, order_id, client_order_id, NULL FROM trades ORDER BY id ASC"
                        )

                    # Swap tables atomically within transaction
                    cursor.execute("DROP TABLE IF EXISTS trades")
                    cursor.execute("ALTER TABLE trades_new RENAME TO trades")
                    conn.commit()
            except sqlite3.Error as e:
                # If migration fails, log and raise so the app doesn't run
                # with an incompatible schema. Developer may need to reset
                # the DB manually in dev environments.
                logger.exception(
                    "Failed to migrate trades table for uniqueness; existing DB may need manual reset."
                )
                raise StateStoreError(
                    "Database schema migration for trades table failed; "
                    "application cannot safely continue."
                ) from e

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

            # Signal gates table for persistent entry gating (one-row per strategy/symbol/action)
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS signal_gates (
                    strategy TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    action TEXT NOT NULL,
                    last_accepted_ts_utc TEXT NOT NULL,
                    last_bar_ts_utc TEXT,
                    PRIMARY KEY (strategy, symbol, action)
                )
            """)

            cursor.execute("""
                CREATE INDEX IF NOT EXISTS idx_signal_gates_symbol
                ON signal_gates(symbol)
            """)

            # Ensure WAL mode and a sensible busy timeout for concurrent access
            try:
                cursor.execute("PRAGMA journal_mode=WAL")
                cursor.execute("PRAGMA busy_timeout=5000")
            except sqlite3.Error:
                # Non-fatal; continue with default settings
                pass

            # Migration: ensure `atr` column exists on order_intents for older DBs
            try:
                cursor.execute("PRAGMA table_info(order_intents)")
                oi_columns = [col[1] for col in cursor.fetchall()]
                if "atr" not in oi_columns:
                    cursor.execute("ALTER TABLE order_intents ADD COLUMN atr NUMERIC(10, 4)")
                    conn.commit()
                # Migration: ensure `filled_avg_price` column exists on order_intents
                if "filled_avg_price" not in oi_columns:
                    cursor.execute(
                        "ALTER TABLE order_intents ADD COLUMN filled_avg_price NUMERIC(10, 4)"
                    )
                    conn.commit()
            except sqlite3.Error as e:
                logger.warning(
                    "Could not migrate order_intents to add atr and filled_avg_price columns: %s",
                    e,
                )

    def get_state(self, key: str) -> Optional[str]:
        """Get state value by key."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT value FROM bot_state WHERE key = ?", (key,))
            row = cursor.fetchone()
            return row[0] if row else None

    def has_open_exposure_increasing_order(self, symbol: str, side: str) -> bool:
        """Return True if an exposure-increasing order intent exists for (symbol, side).

        Considers intents with statuses that indicate an order is in-flight or being processed.
        """
        statuses = ("new", "accepted", "pending_new", "submitted", "partially_filled")
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT 1 FROM order_intents WHERE symbol = ? AND side = ? AND status IN (?,?,?,?,?) LIMIT 1",
                (symbol, side, *statuses),
            )
            return cursor.fetchone() is not None

    def gate_try_accept(
        self,
        strategy: str,
        symbol: str,
        action: str,
        now_utc: datetime,
        bar_ts_utc: Optional[datetime],
        cooldown: timedelta,
        force: bool = False,
    ) -> bool:
        """Atomically try to accept an entry signal for (strategy, symbol, action).

        Non-force behavior: perform a monotonic upsert so older timestamps cannot
        overwrite newer ones. Returns True only when the stored row contains the
        new `now_utc` value after the operation.

        If `force=True` the row is replaced unconditionally (useful for tests/admin).
        """
        import sqlite3 as _sqlite
        from datetime import timezone

        conn = _sqlite.connect(self.db_path, timeout=5.0)
        try:
            conn.isolation_level = None
            cur = conn.cursor()
            try:
                cur.execute("PRAGMA journal_mode=WAL")
                cur.execute("PRAGMA busy_timeout=5000")
            except Exception:
                pass

            # Normalize timestamps to UTC ISO
            last_accepted_iso = now_utc.astimezone(timezone.utc).isoformat()
            last_bar_iso = bar_ts_utc.astimezone(timezone.utc).isoformat() if bar_ts_utc else None

            if force:
                cur.execute(
                    "INSERT OR REPLACE INTO signal_gates (strategy, symbol, action, last_accepted_ts_utc, last_bar_ts_utc) VALUES (?, ?, ?, ?, ?)",
                    (strategy, symbol, action, last_accepted_iso, last_bar_iso),
                )
                conn.commit()
                return True

            # Use an explicit immediate transaction to avoid races between concurrent workers.
            cur.execute("BEGIN IMMEDIATE")
            cur.execute(
                "SELECT last_accepted_ts_utc, last_bar_ts_utc FROM signal_gates WHERE strategy = ? AND symbol = ? AND action = ?",
                (strategy, symbol, action),
            )
            row = cur.fetchone()

            def _parse_iso(ts: Optional[str]) -> Optional[datetime]:
                if not ts:
                    return None
                try:
                    return datetime.fromisoformat(ts).astimezone(timezone.utc)
                except Exception:
                    return None

            stored_last_accepted = _parse_iso(row[0]) if row else None
            stored_last_bar = _parse_iso(row[1]) if row else None

            # Same-bar dedupe
            if bar_ts_utc is not None and stored_last_bar is not None:
                if bar_ts_utc.astimezone(timezone.utc) == stored_last_bar:
                    conn.rollback()
                    return False

            # Cooldown check
            if stored_last_accepted is not None:
                if now_utc.astimezone(timezone.utc) - stored_last_accepted < cooldown:
                    conn.rollback()
                    return False

            # Monotonic upsert: only update if incoming timestamp is newer or row missing.
            upsert_sql = """
                INSERT INTO signal_gates (strategy, symbol, action, last_accepted_ts_utc, last_bar_ts_utc)
                VALUES (?, ?, ?, ?, ?)
                ON CONFLICT(strategy, symbol, action) DO UPDATE SET
                    last_accepted_ts_utc = excluded.last_accepted_ts_utc,
                    last_bar_ts_utc = excluded.last_bar_ts_utc
                WHERE COALESCE(signal_gates.last_accepted_ts_utc, '') <= excluded.last_accepted_ts_utc
            """

            cur.execute(upsert_sql, (strategy, symbol, action, last_accepted_iso, last_bar_iso))

            # Re-read to verify we own the stored timestamp
            cur.execute(
                "SELECT last_accepted_ts_utc FROM signal_gates WHERE strategy = ? AND symbol = ? AND action = ?",
                (strategy, symbol, action),
            )
            row2 = cur.fetchone()
            conn.commit()

            if not row2:
                return False

            return row2[0] == last_accepted_iso
        except _sqlite.Error as e:
            try:
                conn.rollback()
            except Exception:
                pass
            logger.exception("signal gate DB error: %s", e)
            return False
        finally:
            conn.close()

    def get_gate(
        self, strategy: str, symbol: str, action: str
    ) -> Optional[dict[str, Optional[datetime]]]:
        """Return gate row parsed as datetimes or None if missing."""
        with sqlite3.connect(self.db_path) as conn:
            cur = conn.cursor()
            cur.execute(
                "SELECT last_accepted_ts_utc, last_bar_ts_utc FROM signal_gates WHERE strategy = ? AND symbol = ? AND action = ?",
                (strategy, symbol, action),
            )
            row = cur.fetchone()
            if not row:
                return None

            def _parse(ts: Optional[str]) -> Optional[datetime]:
                if not ts:
                    return None
                try:
                    return datetime.fromisoformat(ts).astimezone(timezone.utc)
                except Exception:
                    return None

            return {"last_accepted": _parse(row[0]), "last_bar": _parse(row[1])}

    def release_gate(self, strategy: str, symbol: str, action: str) -> None:
        """Delete the persisted gate row (useful for tests/admin)."""
        with sqlite3.connect(self.db_path) as conn:
            cur = conn.cursor()
            cur.execute(
                "DELETE FROM signal_gates WHERE strategy = ? AND symbol = ? AND action = ?",
                (strategy, symbol, action),
            )
            conn.commit()

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
        filled_qty: Optional[float],
        alpaca_order_id: Optional[str] = None,
        filled_avg_price: Optional[float] = None,
    ) -> None:
        """Update order intent after status change."""
        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            # Coerce alpaca_order_id to a DB-friendly scalar (tests may pass MagicMock)
            if alpaca_order_id is not None and not isinstance(
                alpaca_order_id, (str, bytes, int, float)
            ):
                alpaca_order_id = str(alpaca_order_id)

            cursor.execute(
                """UPDATE order_intents 
                   SET status = ?, filled_qty = COALESCE(?, filled_qty), alpaca_order_id = COALESCE(?, alpaca_order_id), filled_avg_price = COALESCE(?, filled_avg_price), updated_at_utc = ?
                   WHERE client_order_id = ?""",
                (status, filled_qty, alpaca_order_id, filled_avg_price, now, client_order_id),
            )
            conn.commit()

    def get_order_intent(self, client_order_id: str) -> Optional[dict[str, object]]:
        """Get order intent by client_order_id."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT client_order_id, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id FROM order_intents WHERE client_order_id = ?",
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
                    "filled_avg_price": parse_optional_float(row[7]),
                    "alpaca_order_id": row[8],
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
                    "SELECT client_order_id, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id FROM order_intents WHERE status = ?",
                    (status,),
                )
            else:
                cursor.execute(
                    "SELECT client_order_id, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id FROM order_intents"
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
                    "filled_avg_price": parse_optional_float(row[7]),
                    "alpaca_order_id": row[8],
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
