"""SchemaManager: deterministic, idempotent, additive-only SQLite schema migrations.

Owns all DDL for the trading bot database. Runs once at startup BEFORE
StateStore or any other DB consumer is initialised. Ensures tables, columns,
and indexes exist without ever dropping, renaming, or modifying existing
structures.
"""

import logging
import re
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Schema version — bump when adding tables, columns, or indexes
# ---------------------------------------------------------------------------
CURRENT_SCHEMA_VERSION = 4

# ---------------------------------------------------------------------------
# Canonical table definitions (CREATE TABLE IF NOT EXISTS)
# ---------------------------------------------------------------------------
TABLES: dict[str, str] = {
    "schema_meta": """
        CREATE TABLE IF NOT EXISTS schema_meta (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            schema_version INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        )
    """,
    "order_intents": """
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
            updated_at_utc TEXT NOT NULL,
            strategy TEXT DEFAULT ''
        )
    """,
    "trades": """
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
    """,
    "equity_curve": """
        CREATE TABLE IF NOT EXISTS equity_curve (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            equity NUMERIC(12, 2) NOT NULL,
            daily_pnl NUMERIC(12, 2) NOT NULL
        )
    """,
    "bot_state": """
        CREATE TABLE IF NOT EXISTS bot_state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        )
    """,
    "bars": """
        CREATE TABLE IF NOT EXISTS bars (
            symbol TEXT NOT NULL,
            timeframe TEXT NOT NULL,
            timestamp_utc TEXT NOT NULL,
            open NUMERIC(10, 4),
            high NUMERIC(10, 4),
            low NUMERIC(10, 4),
            close NUMERIC(10, 4),
            volume INTEGER,
            trade_count INTEGER,
            vwap NUMERIC(10, 4),
            PRIMARY KEY (symbol, timeframe, timestamp_utc)
        )
    """,
    "positions_snapshot": """
        CREATE TABLE IF NOT EXISTS positions_snapshot (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            symbol TEXT NOT NULL,
            qty NUMERIC(10, 4) NOT NULL,
            avg_entry_price NUMERIC(10, 4) NOT NULL
        )
    """,
    "signal_gates": """
        CREATE TABLE IF NOT EXISTS signal_gates (
            strategy TEXT NOT NULL,
            symbol TEXT NOT NULL,
            action TEXT NOT NULL,
            last_accepted_ts_utc TEXT NOT NULL,
            last_bar_ts_utc TEXT,
            PRIMARY KEY (strategy, symbol, action)
        )
    """,
    "fills": """
        CREATE TABLE IF NOT EXISTS fills (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            alpaca_order_id TEXT NOT NULL,
            client_order_id TEXT NOT NULL,
            symbol TEXT NOT NULL,
            side TEXT NOT NULL,
            delta_qty NUMERIC(10, 4) NOT NULL,
            cum_qty NUMERIC(10, 4) NOT NULL,
            cum_avg_price NUMERIC(10, 4),
            timestamp_utc TEXT NOT NULL,
            fill_id TEXT,
            price_is_estimate INTEGER NOT NULL DEFAULT 1,
            fill_dedupe_key TEXT NOT NULL,
            UNIQUE(alpaca_order_id, fill_dedupe_key)
        )
    """,
    "position_tracking": """
        CREATE TABLE IF NOT EXISTS position_tracking (
            symbol TEXT PRIMARY KEY,
            side TEXT NOT NULL,
            qty NUMERIC(10, 4) NOT NULL,
            entry_price NUMERIC(10, 4) NOT NULL,
            atr NUMERIC(10, 4),
            entry_time TEXT NOT NULL,
            extreme_price NUMERIC(10, 4) NOT NULL,
            trailing_stop_price NUMERIC(10, 4),
            trailing_stop_activated INTEGER DEFAULT 0,
            pending_exit INTEGER DEFAULT 0,
            updated_at TEXT NOT NULL
        )
    """,
    "exit_attempts": """
        CREATE TABLE IF NOT EXISTS exit_attempts (
            symbol TEXT PRIMARY KEY,
            attempt_count INTEGER NOT NULL DEFAULT 0,
            last_attempt_ts_utc TEXT NOT NULL,
            reason TEXT
        )
    """,
    "reconciliation_reports": """
        CREATE TABLE IF NOT EXISTS reconciliation_reports (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            check_type TEXT NOT NULL,
            duration_ms INTEGER NOT NULL,
            discrepancies_count INTEGER NOT NULL DEFAULT 0,
            repaired_count INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL,
            discrepancies_json TEXT,
            repairs_json TEXT,
            error_message TEXT
        )
    """,
}

# ---------------------------------------------------------------------------
# Additive columns — for upgrading older databases that pre-date these columns.
# Each entry: (table, column_name, column_definition)
#
# The column_definition MUST be safe for ALTER TABLE ADD COLUMN:
#   Allowed types: TEXT, INTEGER, REAL, NUMERIC
#   Allowed modifiers: DEFAULT <value>, NOT NULL (only with DEFAULT)
#   Forbidden: PRIMARY KEY, UNIQUE, CHECK, FOREIGN KEY, REFERENCES, AUTOINCREMENT
# ---------------------------------------------------------------------------
ADDITIVE_COLUMNS: list[tuple[str, str, str]] = [
    ("order_intents", "atr", "NUMERIC(10, 4)"),
    ("order_intents", "filled_avg_price", "NUMERIC(10, 4)"),
    ("order_intents", "strategy", "TEXT DEFAULT ''"),
    ("fills", "delta_fill_price", "NUMERIC(10, 4)"),
]

# ---------------------------------------------------------------------------
# Indexes
# ---------------------------------------------------------------------------
INDEXES: dict[str, str] = {
    "idx_order_intents_status": (
        "CREATE INDEX IF NOT EXISTS idx_order_intents_status ON order_intents(status)"
    ),
    "idx_order_intents_symbol": (
        "CREATE INDEX IF NOT EXISTS idx_order_intents_symbol ON order_intents(symbol)"
    ),
    "idx_order_intents_strategy_symbol_side_status": (
        "CREATE INDEX IF NOT EXISTS idx_order_intents_strategy_symbol_side_status "
        "ON order_intents(strategy, symbol, side, status)"
    ),
    "idx_trades_symbol_timestamp": (
        "CREATE INDEX IF NOT EXISTS idx_trades_symbol_timestamp ON trades(symbol, timestamp_utc)"
    ),
    "idx_bars_symbol_timestamp": (
        "CREATE INDEX IF NOT EXISTS idx_bars_symbol_timestamp ON bars(symbol, timestamp_utc)"
    ),
    "idx_positions_snapshot_timestamp": (
        "CREATE INDEX IF NOT EXISTS idx_positions_snapshot_timestamp "
        "ON positions_snapshot(timestamp_utc)"
    ),
    "idx_equity_curve_timestamp": (
        "CREATE INDEX IF NOT EXISTS idx_equity_curve_timestamp ON equity_curve(timestamp_utc)"
    ),
    "idx_signal_gates_symbol": (
        "CREATE INDEX IF NOT EXISTS idx_signal_gates_symbol ON signal_gates(symbol)"
    ),
    "idx_fills_alpaca_order_id": (
        "CREATE INDEX IF NOT EXISTS idx_fills_alpaca_order_id ON fills(alpaca_order_id)"
    ),
    "idx_fills_client_order_id": (
        "CREATE INDEX IF NOT EXISTS idx_fills_client_order_id ON fills(client_order_id)"
    ),
    "idx_fills_symbol": ("CREATE INDEX IF NOT EXISTS idx_fills_symbol ON fills(symbol)"),
    "idx_fills_timestamp": (
        "CREATE INDEX IF NOT EXISTS idx_fills_timestamp ON fills(timestamp_utc)"
    ),
    "idx_order_intents_alpaca_order_id": (
        "CREATE INDEX IF NOT EXISTS idx_order_intents_alpaca_order_id "
        "ON order_intents(alpaca_order_id)"
    ),
    "idx_position_tracking_pending_exit": (
        "CREATE INDEX IF NOT EXISTS idx_position_tracking_pending_exit "
        "ON position_tracking(pending_exit)"
    ),
    "idx_reconciliation_reports_timestamp": (
        "CREATE INDEX IF NOT EXISTS idx_reconciliation_reports_timestamp "
        "ON reconciliation_reports(timestamp_utc)"
    ),
    "idx_reconciliation_reports_status": (
        "CREATE INDEX IF NOT EXISTS idx_reconciliation_reports_status "
        "ON reconciliation_reports(status)"
    ),
}

# Tokens that make a column definition unsafe for ALTER TABLE ADD COLUMN
_UNSAFE_TOKENS = re.compile(
    r"\b(PRIMARY\s+KEY|UNIQUE|CHECK|FOREIGN\s+KEY|REFERENCES|AUTOINCREMENT)\b",
    re.IGNORECASE,
)


class SchemaError(Exception):
    """Raised when schema migration fails — startup must abort."""

    pass


class SchemaManager:
    """Deterministic, idempotent, additive-only SQLite schema manager."""

    @staticmethod
    def _is_safe_column_def(col_def: str) -> bool:
        """Return True if the column definition is safe for ALTER TABLE ADD COLUMN."""
        if _UNSAFE_TOKENS.search(col_def):
            return False
        # NOT NULL without DEFAULT is unsafe (existing rows would violate it)
        upper = col_def.upper()
        if "NOT NULL" in upper and "DEFAULT" not in upper:
            return False
        return True

    @staticmethod
    def _quote_ident(name: str) -> str:
        """Return a safely-quoted SQLite identifier.

        SQLite identifiers are double-quoted; embedded double quotes are
        escaped by doubling them. This helper ensures identifiers used in
        PRAGMA/ALTER statements are properly quoted.
        """
        return '"' + name.replace('"', '""') + '"'

    @staticmethod
    def _get_table_columns(cursor: sqlite3.Cursor, table: str) -> set[str]:
        """Return the set of column names for a table."""
        cursor.execute(f"PRAGMA table_info({SchemaManager._quote_ident(table)})")  # noqa: S608
        return {row[1] for row in cursor.fetchall()}

    @staticmethod
    def _get_existing_tables(cursor: sqlite3.Cursor) -> set[str]:
        """Return the set of user table names in the database."""
        cursor.execute("SELECT name FROM sqlite_master WHERE type='table'")
        return {row[0] for row in cursor.fetchall()}

    @staticmethod
    def _get_existing_indexes(cursor: sqlite3.Cursor) -> set[str]:
        """Return the set of index names in the database."""
        cursor.execute("SELECT name FROM sqlite_master WHERE type='index'")
        return {row[0] for row in cursor.fetchall()}

    @staticmethod
    def _backup_if_needed(db_path: str, changes: list[str], dry_run: bool) -> Optional[str]:
        """Create a backup before applying schema changes.

        Returns the backup path if a backup was created, None otherwise.
        Only backs up when the DB file exists and changes are pending.
        """
        if dry_run or not changes:
            return None

        db_file = Path(db_path)
        if not db_file.exists() or db_file.stat().st_size == 0:
            return None

        backup_dir = db_file.parent / "db_backups"
        backup_dir.mkdir(exist_ok=True)
        timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
        backup_path = backup_dir / f"{db_file.stem}.{timestamp}.bak"

        # Use SQLite's backup API instead of raw file copy to ensure a
        # consistent snapshot even when WAL mode is enabled.
        with sqlite3.connect(db_path) as src_conn, sqlite3.connect(str(backup_path)) as dst_conn:
            src_conn.backup(dst_conn)
        if not backup_path.exists() or backup_path.stat().st_size == 0:
            raise SchemaError(f"Schema backup failed: {backup_path} missing or empty")

        logger.info("[SchemaManager] Backup created: %s", backup_path)
        return str(backup_path)

    @classmethod
    def ensure_schema(cls, db_path: str, dry_run: bool = False) -> list[str]:
        """Ensure the database schema is up to date.

        Creates missing tables, adds missing columns, creates missing indexes,
        and updates the schema version. All changes are applied in a single
        transaction with an early write lock (BEGIN IMMEDIATE).

        Args:
            db_path: Path to the SQLite database file.
            dry_run: If True, plan changes but do not apply them.

        Returns:
            List of human-readable descriptions of changes made (or planned).

        Raises:
            SchemaError: On any failure — caller must abort startup.
        """
        Path(db_path).parent.mkdir(parents=True, exist_ok=True)
        planned_actions: list[str] = []

        with sqlite3.connect(db_path) as conn:
            try:
                cursor = conn.cursor()

                # Set PRAGMAs before transaction. Avoid persistent changes and write
                # locks when running in dry-run mode so the operation has no side
                # effects and does not block other DB users.
                if not dry_run:
                    cursor.execute("PRAGMA journal_mode=WAL")
                    cursor.execute("PRAGMA busy_timeout=5000")

                    # Acquire write lock early
                    cursor.execute("BEGIN IMMEDIATE")
                else:
                    # Still configure busy_timeout for consistent behavior, but do
                    # not alter journal mode or acquire a write lock.
                    cursor.execute("PRAGMA busy_timeout=5000")
                    # Start a deferred transaction so dry-run DDL can be rolled back
                    cursor.execute("BEGIN")

                # ------ schema_meta table (always create first) ------
                existing_tables = cls._get_existing_tables(cursor)
                if "schema_meta" not in existing_tables:
                    cursor.execute(TABLES["schema_meta"])
                    # Don't log schema_meta creation as a user-visible change

                # ------ Version check ------
                cursor.execute("SELECT schema_version FROM schema_meta WHERE id = 1")
                row = cursor.fetchone()
                stored_version: Optional[int] = row[0] if row else None

                if stored_version is not None and stored_version > CURRENT_SCHEMA_VERSION:
                    conn.rollback()
                    raise SchemaError(
                        f"Database schema version ({stored_version}) is newer than "
                        f"code version ({CURRENT_SCHEMA_VERSION}). "
                        f"Upgrade the application or restore from backup."
                    )

                # Re-read tables after schema_meta creation
                existing_tables = cls._get_existing_tables(cursor)

                # ------ Create missing tables ------
                for table_name in sorted(TABLES.keys()):
                    if table_name == "schema_meta":
                        continue  # Already handled
                    if table_name not in existing_tables:
                        cursor.execute(TABLES[table_name])
                        action = f"Created table {table_name}"
                        planned_actions.append(action)
                        logger.info("[SchemaManager] %s", action)

                # ------ Add missing columns (sorted deterministically) ------
                sorted_columns = sorted(ADDITIVE_COLUMNS, key=lambda c: (c[0], c[1]))
                # Cache table -> set(column names) to avoid repeated PRAGMA calls
                existing_cols_cache: dict[str, set[str]] = {}
                for table, col_name, col_def in sorted_columns:
                    if not cls._is_safe_column_def(col_def):
                        logger.error(
                            "[SchemaManager] Unsafe column definition encountered: %s.%s %s",
                            table,
                            col_name,
                            col_def,
                        )
                        conn.rollback()
                        raise SchemaError(
                            f"Unsafe column definition for {table}.{col_name}: {col_def}"
                        )

                    existing_cols = existing_cols_cache.get(table)
                    if existing_cols is None:
                        existing_cols = cls._get_table_columns(cursor, table)
                        existing_cols_cache[table] = existing_cols
                    if col_name not in existing_cols:
                        sql = f"ALTER TABLE {cls._quote_ident(table)} ADD COLUMN {cls._quote_ident(col_name)} {col_def}"
                        cursor.execute(sql)
                        # Update cached columns immediately so subsequent
                        # iterations for the same table don't re-query PRAGMA.
                        existing_cols.add(col_name)
                        action = f"Added column {table}.{col_name}"
                        planned_actions.append(action)
                        logger.info("[SchemaManager] %s", action)

                # ------ Create missing indexes ------
                existing_indexes = cls._get_existing_indexes(cursor)
                for idx_name in sorted(INDEXES.keys()):
                    if idx_name not in existing_indexes:
                        cursor.execute(INDEXES[idx_name])
                        action = f"Created index {idx_name}"
                        planned_actions.append(action)
                        logger.info("[SchemaManager] %s", action)

                # ------ Detect non-additive drift on trades table ------
                # The canonical trades schema requires UNIQUE constraints on
                # (order_id, fill_id) and (order_id, client_order_id). These
                # cannot be added via ALTER TABLE. If the table exists but lacks
                # them, warn and abort.
                if "trades" in existing_tables and "trades" not in [
                    a.split()[-1] for a in planned_actions if "Created table" in a
                ]:
                    trades_cols = cls._get_table_columns(cursor, "trades")
                    if "fill_id" not in trades_cols:
                        conn.rollback()
                        raise SchemaError(
                            "trades table exists but lacks the fill_id column and "
                            "required UNIQUE constraints. This requires a manual "
                            "migration (table rebuild). See prior state_store.py "
                            "migration logic for reference."
                        )

                    # Verify required UNIQUE constraints (or equivalent unique indexes)
                    # on (order_id, fill_id) and (order_id, client_order_id).
                    cursor.execute("PRAGMA index_list(trades)")
                    index_rows = cursor.fetchall()

                    unique_index_columns = []
                    for row in index_rows:
                        # row layout: seq, name, unique, origin, partial, ...
                        if len(row) >= 3 and row[2]:
                            idx_name = row[1]
                            # idx_name is obtained from SQLite metadata; still quote defensively.
                            cursor.execute(f"PRAGMA index_info({cls._quote_ident(idx_name)})")
                            cols = [info_row[2] for info_row in cursor.fetchall()]
                            unique_index_columns.append(cols)

                    # Required UNIQUE constraints (column order is not semantically significant
                    # for drift detection, so we will compare using unordered sets of columns).
                    required_pairs = [
                        ["order_id", "fill_id"],
                        ["order_id", "client_order_id"],
                    ]

                    unique_index_sets = {frozenset(cols) for cols in unique_index_columns}
                    missing_pairs = [
                        pair for pair in required_pairs if frozenset(pair) not in unique_index_sets
                    ]

                    if missing_pairs:
                        conn.rollback()
                        raise SchemaError(
                            "trades table exists but lacks required UNIQUE constraints "
                            "on (order_id, fill_id) and/or (order_id, client_order_id). "
                            "This requires a manual migration (table rebuild). See prior "
                            "state_store.py migration logic for reference."
                        )
                # ------ Update schema version ------
                now = datetime.now(timezone.utc).isoformat()
                if stored_version is None:
                    cursor.execute(
                        "INSERT INTO schema_meta (id, schema_version, updated_at) "
                        "VALUES (1, ?, ?)",
                        (CURRENT_SCHEMA_VERSION, now),
                    )
                    action = f"Set schema version to {CURRENT_SCHEMA_VERSION}"
                    planned_actions.append(action)
                    logger.info("[SchemaManager] %s", action)
                elif stored_version < CURRENT_SCHEMA_VERSION:
                    cursor.execute(
                        "UPDATE schema_meta SET schema_version = ?, updated_at = ? " "WHERE id = 1",
                        (CURRENT_SCHEMA_VERSION, now),
                    )
                    action = (
                        f"Schema upgraded from v{stored_version} " f"to v{CURRENT_SCHEMA_VERSION}"
                    )
                    planned_actions.append(action)
                    logger.info("[SchemaManager] %s", action)

                # ------ Backup + Commit / Rollback ------
                if dry_run:
                    conn.rollback()
                    if planned_actions:
                        logger.info(
                            "[SchemaManager] Dry-run: %d change(s) planned",
                            len(planned_actions),
                        )
                    else:
                        logger.info("[SchemaManager] Dry-run: schema up to date")
                else:
                    # Backup before committing if there are changes and DB exists
                    cls._backup_if_needed(db_path, planned_actions, dry_run)
                    conn.commit()
                    if planned_actions:
                        logger.info(
                            "[SchemaManager] Schema updated (%d change(s))",
                            len(planned_actions),
                        )
                    else:
                        logger.info(
                            "[SchemaManager] Schema up to date (v%d)", CURRENT_SCHEMA_VERSION
                        )

            except SchemaError:
                raise
            except Exception as e:
                try:
                    conn.rollback()
                except Exception:
                    pass
                raise SchemaError(f"Schema migration failed: {e}") from e

        return planned_actions
