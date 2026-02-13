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
    strategy: Optional[str]
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
        """Set connection-level PRAGMAs.

        Table and index creation is handled by SchemaManager at startup.
        This method only configures WAL mode and busy timeout for the
        connection used during StateStore initialisation.
        """
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            try:
                cursor.execute("PRAGMA journal_mode=WAL")
            except sqlite3.Error as e:
                logger.debug("Failed to set SQLite journal_mode=WAL during init: %s", e)
            try:
                cursor.execute("PRAGMA busy_timeout=5000")
            except sqlite3.Error as e:
                logger.debug("Failed to set SQLite busy_timeout during init: %s", e)

    def get_state(self, key: str) -> Optional[str]:
        """Get state value by key."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT value FROM bot_state WHERE key = ?", (key,))
            row = cursor.fetchone()
            return row[0] if row else None

    def has_open_exposure_increasing_order(
        self, symbol: str, side: str, strategy: Optional[str] = None
    ) -> bool:
        """Return True if an exposure-increasing order intent exists.

        If `strategy` is provided, scope the query to that strategy. Otherwise
        behaves like the previous global check.
        """
        statuses = ("new", "accepted", "pending_new", "submitted", "partially_filled")
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            # If a strategy is provided, scope the query to that strategy to avoid
            # cross-strategy blocking when multiple strategies share the same account.
            if strategy:
                cursor.execute(
                    "SELECT 1 FROM order_intents WHERE strategy = ? AND symbol = ? AND side = ? AND status IN (?,?,?,?,?) LIMIT 1",
                    (strategy, symbol, side, *statuses),
                )
            else:
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
                # Only set per-connection busy timeout on this hot path. The
                # journal_mode (WAL) is configured once during `init_schema()`
                # to avoid the overhead of repeatedly setting it here.
                cur.execute("PRAGMA busy_timeout=5000")
            except _sqlite.Error:
                # Best-effort PRAGMA; do not fail the gate on PRAGMA issues.
                logger.debug(
                    "Failed to apply SQLite PRAGMA busy_timeout for signal gate.", exc_info=True
                )

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
            # Monotonic upsert SQL: only overwrite the stored `last_accepted_ts_utc`
            # when the incoming `excluded.last_accepted_ts_utc` is newer (greater).
            #
            # Note for maintainers/tests: this behavior is subtle and should be
            # covered by a unit test. A good test would:
            # 1) call `gate_try_accept` with `now_utc = t2` and assert it returns True
            #    and stores `t2`.
            # 2) call `gate_try_accept` with an older `now_utc = t1 < t2` and assert
            #    it returns False and that the stored timestamp remains `t2`.
            # This will catch regressions in the COALESCE/strftime comparison logic
            # and protect against older events overwriting newer gate timestamps.
            upsert_sql = """
                INSERT INTO signal_gates (strategy, symbol, action, last_accepted_ts_utc, last_bar_ts_utc)
                VALUES (?, ?, ?, ?, ?)
                ON CONFLICT(strategy, symbol, action) DO UPDATE SET
                    last_accepted_ts_utc = excluded.last_accepted_ts_utc,
                    last_bar_ts_utc = excluded.last_bar_ts_utc
                WHERE COALESCE(
                    CAST(strftime('%s', signal_gates.last_accepted_ts_utc) AS REAL),
                    0
                ) <= CAST(strftime('%s', excluded.last_accepted_ts_utc) AS REAL)
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

            # row2[0] may be Any from sqlite; coerce to str and ensure a bool is returned
            stored_val: str = str(row2[0])
            try:
                # Compare normalized datetimes (in UTC) rather than raw ISO strings to
                # avoid issues with differing ISO formatting (e.g., fractional seconds).
                stored_dt = datetime.fromisoformat(stored_val).astimezone(timezone.utc)
                new_dt = now_utc.astimezone(timezone.utc)
                accepted: bool = stored_dt == new_dt
            except (TypeError, ValueError):
                # Fallback to string comparison if parsing fails for any reason.
                accepted = stored_val == last_accepted_iso
            return accepted
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
        strategy: str = "",
    ) -> None:
        """Save order intent before submission (crash safety)."""
        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                """INSERT INTO order_intents 
                   (client_order_id, strategy, symbol, side, qty, atr, status, created_at_utc, updated_at_utc)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (client_order_id, strategy, symbol, side, qty, atr, status, now, now),
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
                "SELECT client_order_id, strategy, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id FROM order_intents WHERE client_order_id = ?",
                (client_order_id,),
            )
            row = cursor.fetchone()
            if row:
                atr_val = parse_optional_float(row[5])
                # Convert required numeric fields to float and optional ones
                # via the shared helper to avoid leaking Decimal/str values.
                return {
                    "client_order_id": row[0],
                    "strategy": row[1],
                    "symbol": row[2],
                    "side": row[3],
                    "qty": float(row[4]),
                    "atr": atr_val,
                    "status": row[6],
                    "filled_qty": parse_optional_float(row[7]),
                    "filled_avg_price": parse_optional_float(row[8]),
                    "alpaca_order_id": row[9],
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
                    "SELECT client_order_id, strategy, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id FROM order_intents WHERE status = ?",
                    (status,),
                )
            else:
                cursor.execute(
                    "SELECT client_order_id, strategy, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id FROM order_intents"
                )

            rows = cursor.fetchall()

            def map_row(row: tuple[Any, ...]) -> OrderIntentRow:
                atr_val = parse_optional_float(row[5])
                return {
                    "client_order_id": row[0],
                    "strategy": row[1],
                    "symbol": row[2],
                    "side": row[3],
                    "qty": float(row[4]),
                    "atr": atr_val,
                    "status": row[6],
                    "filled_qty": parse_optional_float(row[7]),
                    "filled_avg_price": parse_optional_float(row[8]),
                    "alpaca_order_id": row[9],
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

    # ------------------------------------------------------------------
    # Partial-fill support
    # ------------------------------------------------------------------

    def get_order_intent_by_alpaca_id(self, alpaca_order_id: str) -> Optional[dict[str, object]]:
        """Get order intent by Alpaca order ID (primary lookup for fills)."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT client_order_id, strategy, symbol, side, qty, atr, "
                "status, filled_qty, filled_avg_price, alpaca_order_id "
                "FROM order_intents WHERE alpaca_order_id = ? LIMIT 1",
                (alpaca_order_id,),
            )
            row = cursor.fetchone()
            if row:
                return {
                    "client_order_id": row[0],
                    "strategy": row[1],
                    "symbol": row[2],
                    "side": row[3],
                    "qty": float(row[4]),
                    "atr": parse_optional_float(row[5]),
                    "status": row[6],
                    "filled_qty": parse_optional_float(row[7]),
                    "filled_avg_price": parse_optional_float(row[8]),
                    "alpaca_order_id": row[9],
                }
            return None

    def get_last_cum_qty_for_order(self, alpaca_order_id: str) -> float:
        """Return the last-known cumulative filled qty for an order.

        Prefers order_intents.filled_qty (fast). Falls back to
        MAX(fills.cum_qty) for audit trail consistency.
        """
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            # Fast path: order_intents
            cursor.execute(
                "SELECT filled_qty FROM order_intents " "WHERE alpaca_order_id = ? LIMIT 1",
                (alpaca_order_id,),
            )
            row = cursor.fetchone()
            if row and row[0] is not None:
                try:
                    return float(row[0])
                except (TypeError, ValueError):
                    pass

            # Audit fallback: fills table
            cursor.execute(
                "SELECT MAX(cum_qty) FROM fills WHERE alpaca_order_id = ?",
                (alpaca_order_id,),
            )
            row = cursor.fetchone()
            if row and row[0] is not None:
                try:
                    return float(row[0])
                except (TypeError, ValueError):
                    pass

        return 0.0

    def insert_fill_idempotent(
        self,
        alpaca_order_id: str,
        client_order_id: str,
        symbol: str,
        side: str,
        delta_qty: float,
        cum_qty: float,
        cum_avg_price: Optional[float],
        timestamp_utc: str,
        fill_id: Optional[str] = None,
        price_is_estimate: bool = True,
        delta_fill_price: Optional[float] = None,
    ) -> bool:
        """Insert a fill row idempotently. Returns True if inserted, False if duplicate.

        Uses a computed fill_dedupe_key for the unique constraint:
        - If fill_id is present: uses the fill_id
        - If fill_id is absent: uses 'CUM:<cum_qty>' as the key

        Args:
            delta_fill_price: Incremental fill price (preferred over cumulative avg)
        """
        dedupe_key = fill_id if fill_id else f"CUM:{cum_qty}"
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            try:
                cursor.execute(
                    """INSERT INTO fills
                       (alpaca_order_id, client_order_id, symbol, side,
                        delta_qty, cum_qty, cum_avg_price, timestamp_utc,
                        fill_id, price_is_estimate, fill_dedupe_key, delta_fill_price)
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                    (
                        alpaca_order_id,
                        client_order_id,
                        symbol,
                        side,
                        delta_qty,
                        cum_qty,
                        cum_avg_price,
                        timestamp_utc,
                        fill_id,
                        1 if price_is_estimate else 0,
                        dedupe_key,
                        delta_fill_price,
                    ),
                )
                conn.commit()
                return True
            except sqlite3.IntegrityError:
                # Duplicate — unique constraint on (alpaca_order_id, fill_dedupe_key)
                return False

    def update_order_intent_cumulative(
        self,
        alpaca_order_id: str,
        status: str,
        new_cum_qty: float,
        new_cum_avg_price: Optional[float],
        timestamp_utc: str,
    ) -> None:
        """Update order intent with monotonically increasing cumulative fill data.

        Only increases filled_qty (never decreases). Status and timestamp are
        always updated.
        """
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            # Monotonic update: only set filled_qty when new value >= existing.
            # Status/timestamp are always updated.
            cursor.execute(
                """UPDATE order_intents
                   SET status = ?,
                       filled_qty = CASE
                           WHEN COALESCE(filled_qty, 0) < ? THEN ?
                           ELSE filled_qty
                       END,
                       filled_avg_price = CASE
                           WHEN COALESCE(filled_qty, 0) < ? THEN COALESCE(?, filled_avg_price)
                           ELSE filled_avg_price
                       END,
                       updated_at_utc = ?
                   WHERE alpaca_order_id = ?""",
                (
                    status,
                    new_cum_qty,
                    new_cum_qty,
                    new_cum_qty,
                    new_cum_avg_price,
                    timestamp_utc,
                    alpaca_order_id,
                ),
            )
            conn.commit()

    # ------------------------------------------------------------------
    # Exit retry backoff tracking
    # ------------------------------------------------------------------

    def record_exit_attempt(self, symbol: str, reason: str) -> None:
        """Record an exit attempt for a symbol (upsert to exit_attempts table).

        Increments attempt_count and updates last_attempt_ts_utc.

        Args:
            symbol: Stock symbol
            reason: Exit reason (e.g., "stop_loss", "profit_target")
        """
        now_utc = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                """INSERT INTO exit_attempts (symbol, attempt_count, last_attempt_ts_utc, reason)
                   VALUES (?, 1, ?, ?)
                   ON CONFLICT(symbol) DO UPDATE SET
                       attempt_count = attempt_count + 1,
                       last_attempt_ts_utc = excluded.last_attempt_ts_utc,
                       reason = excluded.reason""",
                (symbol, now_utc, reason),
            )
            conn.commit()

    def get_exit_backoff_seconds(self, symbol: str) -> int:
        """Compute exponential backoff for exit retries.

        Returns backoff seconds based on attempt count:
        - Progression: 30s, 60s, 120s, 240s, 480s, 600s (max)
        - Reset after 1 hour of no attempts

        Args:
            symbol: Stock symbol

        Returns:
            Backoff seconds (0 if no backoff or reset)
        """
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT attempt_count, last_attempt_ts_utc FROM exit_attempts WHERE symbol = ?",
                (symbol,),
            )
            row = cursor.fetchone()
            if not row:
                return 0

            attempt_count = int(row[0])
            last_attempt_ts_str = row[1]

            try:
                last_attempt_ts = datetime.fromisoformat(last_attempt_ts_str).astimezone(
                    timezone.utc
                )
            except (ValueError, TypeError):
                return 0

            now_utc = datetime.now(timezone.utc)
            elapsed_seconds = (now_utc - last_attempt_ts).total_seconds()

            # Reset after 1 hour
            if elapsed_seconds > 3600:
                return 0

            # Exponential backoff: 30s * 2^(attempt-1), capped at 600s
            backoff = int(min(30 * (2 ** (attempt_count - 1)), 600))
            return backoff

    def can_retry_exit(self, symbol: str) -> bool:
        """Check if exit retry backoff period has elapsed.

        Args:
            symbol: Stock symbol

        Returns:
            True if retry is allowed, False if backoff active
        """
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT last_attempt_ts_utc FROM exit_attempts WHERE symbol = ?",
                (symbol,),
            )
            row = cursor.fetchone()
            if not row:
                # No attempts recorded - allowed
                return True

            last_attempt_ts_str = row[0]
            try:
                last_attempt_ts = datetime.fromisoformat(last_attempt_ts_str).astimezone(
                    timezone.utc
                )
            except (ValueError, TypeError):
                return True

            now_utc = datetime.now(timezone.utc)
            elapsed_seconds = (now_utc - last_attempt_ts).total_seconds()

            backoff = self.get_exit_backoff_seconds(symbol)
            return elapsed_seconds >= backoff
