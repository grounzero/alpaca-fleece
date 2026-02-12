"""Tests for SchemaManager — additive-only SQLite schema migrations."""

import sqlite3

import pytest

from src.schema_manager import (
    CURRENT_SCHEMA_VERSION,
    INDEXES,
    TABLES,
    SchemaError,
    SchemaManager,
)


class TestFreshDatabase:
    """Schema creation on an empty database."""

    def test_creates_all_tables(self, tmp_path):
        db = str(tmp_path / "fresh.db")
        SchemaManager.ensure_schema(db)

        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("SELECT name FROM sqlite_master WHERE type='table'")
            tables = {row[0] for row in cur.fetchall()}

        for table_name in TABLES:
            assert table_name in tables, f"Missing table: {table_name}"

    def test_creates_all_indexes(self, tmp_path):
        db = str(tmp_path / "fresh.db")
        SchemaManager.ensure_schema(db)

        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("SELECT name FROM sqlite_master WHERE type='index'")
            indexes = {row[0] for row in cur.fetchall()}

        for idx_name in INDEXES:
            assert idx_name in indexes, f"Missing index: {idx_name}"

    def test_sets_schema_version(self, tmp_path):
        db = str(tmp_path / "fresh.db")
        SchemaManager.ensure_schema(db)

        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("SELECT schema_version FROM schema_meta WHERE id = 1")
            version = cur.fetchone()[0]

        assert version == CURRENT_SCHEMA_VERSION

    def test_returns_changes_for_fresh_db(self, tmp_path):
        db = str(tmp_path / "fresh.db")
        changes = SchemaManager.ensure_schema(db)
        # Should have created all non-meta tables + all indexes + version set
        assert len(changes) > 0
        assert any("Created table" in c for c in changes)


class TestMissingColumn:
    """Adding missing columns to existing tables."""

    def test_adds_missing_atr_column(self, tmp_path):
        db = str(tmp_path / "old.db")

        # Create order_intents without the atr column
        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("""
                CREATE TABLE order_intents (
                    client_order_id TEXT PRIMARY KEY,
                    symbol TEXT NOT NULL,
                    side TEXT NOT NULL,
                    qty NUMERIC(10, 4) NOT NULL,
                    status TEXT NOT NULL,
                    filled_qty NUMERIC(10, 4) DEFAULT 0,
                    alpaca_order_id TEXT,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                )
            """)

        changes = SchemaManager.ensure_schema(db)

        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("PRAGMA table_info(order_intents)")
            cols = {row[1] for row in cur.fetchall()}

        assert "atr" in cols
        assert "filled_avg_price" in cols
        assert "strategy" in cols
        assert any("Added column order_intents.atr" in c for c in changes)

    def test_adds_only_missing_columns(self, tmp_path):
        """If some columns already exist, only the missing ones are added."""
        db = str(tmp_path / "partial.db")

        # Create order_intents with atr but without strategy
        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("""
                CREATE TABLE order_intents (
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

        changes = SchemaManager.ensure_schema(db)

        column_changes = [c for c in changes if "Added column" in c]
        # atr should NOT be in changes (already exists)
        assert not any("order_intents.atr" in c for c in column_changes)
        # strategy and filled_avg_price should be added
        assert any("order_intents.strategy" in c for c in column_changes)
        assert any("order_intents.filled_avg_price" in c for c in column_changes)


class TestIdempotent:
    """Running ensure_schema multiple times produces identical state."""

    def test_second_run_makes_no_changes(self, tmp_path):
        db = str(tmp_path / "idem.db")

        changes1 = SchemaManager.ensure_schema(db)
        assert len(changes1) > 0

        changes2 = SchemaManager.ensure_schema(db)
        assert changes2 == []

    def test_dry_run_after_ensure_reports_no_changes(self, tmp_path):
        db = str(tmp_path / "idem2.db")
        SchemaManager.ensure_schema(db)

        planned = SchemaManager.ensure_schema(db, dry_run=True)
        assert planned == []


class TestDryRun:
    """Dry-run mode plans but does not apply changes."""

    def test_dry_run_on_fresh_db_returns_planned_actions(self, tmp_path):
        db = str(tmp_path / "dry.db")
        planned = SchemaManager.ensure_schema(db, dry_run=True)
        assert len(planned) > 0
        assert any("Created table" in p for p in planned)

    def test_dry_run_does_not_persist_tables(self, tmp_path):
        db = str(tmp_path / "dry2.db")
        SchemaManager.ensure_schema(db, dry_run=True)

        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("SELECT name FROM sqlite_master WHERE type='table'")
            tables = {row[0] for row in cur.fetchall()}

        # Only schema_meta might exist from the initial check, but the
        # transaction was rolled back so nothing should persist.
        # With BEGIN IMMEDIATE and rollback, no tables should exist.
        assert "order_intents" not in tables
        assert "trades" not in tables


class TestVersionMismatch:
    """Database version newer than code version."""

    def test_raises_on_newer_db_version(self, tmp_path):
        db = str(tmp_path / "future.db")

        # Create schema_meta with a future version
        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("""
                CREATE TABLE schema_meta (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    schema_version INTEGER NOT NULL,
                    updated_at TEXT NOT NULL
                )
            """)
            cur.execute(
                "INSERT INTO schema_meta VALUES (1, ?, '2025-01-01T00:00:00')",
                (CURRENT_SCHEMA_VERSION + 100,),
            )

        with pytest.raises(SchemaError, match="newer than code version"):
            SchemaManager.ensure_schema(db)


class TestBackup:
    """Backup behaviour before schema changes."""

    def test_backup_created_when_changes_needed(self, tmp_path):
        db = str(tmp_path / "backup.db")
        # Create a partial DB so the file exists and changes are needed
        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("""
                CREATE TABLE bot_state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                )
            """)

        SchemaManager.ensure_schema(db)

        backup_dir = tmp_path / "db_backups"
        assert backup_dir.exists()
        backups = list(backup_dir.glob("*.bak"))
        assert len(backups) == 1
        assert backups[0].stat().st_size > 0

    def test_no_backup_when_up_to_date(self, tmp_path):
        db = str(tmp_path / "current.db")
        SchemaManager.ensure_schema(db)

        # Second run — no changes, no backup
        SchemaManager.ensure_schema(db)

        backup_dir = tmp_path / "db_backups"
        if backup_dir.exists():
            backups = list(backup_dir.glob("*.bak"))
            # At most one backup from the first run (fresh DB has no file to back up)
            # but second run should not add another
            assert len(backups) <= 1


class TestWALMode:
    """WAL journal mode is set."""

    def test_wal_mode_enabled(self, tmp_path):
        db = str(tmp_path / "wal.db")
        SchemaManager.ensure_schema(db)

        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("PRAGMA journal_mode")
            mode = cur.fetchone()[0]

        assert mode == "wal"


class TestDeterministicOrder:
    """Columns are applied in alphabetical order."""

    def test_columns_applied_alphabetically(self, tmp_path):
        db = str(tmp_path / "order.db")

        # Create order_intents missing all additive columns
        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("""
                CREATE TABLE order_intents (
                    client_order_id TEXT PRIMARY KEY,
                    symbol TEXT NOT NULL,
                    side TEXT NOT NULL,
                    qty NUMERIC(10, 4) NOT NULL,
                    status TEXT NOT NULL,
                    filled_qty NUMERIC(10, 4) DEFAULT 0,
                    alpaca_order_id TEXT,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                )
            """)

        changes = SchemaManager.ensure_schema(db)

        # Extract column addition changes
        col_changes = [c for c in changes if "Added column" in c]
        col_names = [c.split(".")[-1] for c in col_changes]

        # Verify alphabetical: atr, filled_avg_price, strategy
        assert col_names == sorted(col_names)


class TestUnsafeColumnSkipped:
    """Unsafe column definitions are skipped."""

    def test_unique_column_skipped(self, tmp_path, monkeypatch):
        """A column def containing UNIQUE is rejected."""
        from src import schema_manager

        monkeypatch.setattr(
            schema_manager,
            "ADDITIVE_COLUMNS",
            [("bot_state", "bad_col", "TEXT UNIQUE")],
        )

        db = str(tmp_path / "unsafe.db")
        with pytest.raises(SchemaError, match="Unsafe column definition"):
            SchemaManager.ensure_schema(db)

    def test_primary_key_column_skipped(self, tmp_path, monkeypatch):
        """A column def containing PRIMARY KEY is rejected."""
        from src import schema_manager

        monkeypatch.setattr(
            schema_manager,
            "ADDITIVE_COLUMNS",
            [("bot_state", "pk_col", "INTEGER PRIMARY KEY")],
        )

        db = str(tmp_path / "unsafe_pk.db")
        with pytest.raises(SchemaError, match="Unsafe column definition"):
            SchemaManager.ensure_schema(db)


class TestTradesDrift:
    """Detect non-additive drift on the trades table."""

    def test_trades_without_fill_id_aborts(self, tmp_path):
        db = str(tmp_path / "drift.db")

        # Create trades table without fill_id column
        with sqlite3.connect(db) as conn:
            cur = conn.cursor()
            cur.execute("""
                CREATE TABLE trades (
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

        with pytest.raises(SchemaError, match="fill_id"):
            SchemaManager.ensure_schema(db)
