import sqlite3
import tempfile
from pathlib import Path

import pytest

from src.schema_manager import SchemaManager
from src.state_store import StateStore


def create_old_schema(db_path: str) -> None:
    """Create an older order_intents schema without the `atr` column."""
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute("""
            CREATE TABLE IF NOT EXISTS order_intents (
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
        conn.commit()


def test_migration_adds_atr_and_persists():
    with tempfile.TemporaryDirectory() as td:
        db_path = Path(td) / "state.db"
        # create old schema without atr
        create_old_schema(str(db_path))

        # SchemaManager should add missing columns
        SchemaManager.ensure_schema(str(db_path))
        store = StateStore(str(db_path))

        # ensure migration added the column by inserting a row with atr
        client_order_id = "test-1"
        store.save_order_intent(
            client_order_id=client_order_id,
            symbol="AAPL",
            side="buy",
            qty=1.0,
            status="new",
            atr=0.42,
        )

        row = store.get_order_intent(client_order_id)
        assert row is not None
        assert row["client_order_id"] == client_order_id
        assert row["atr"] == pytest.approx(0.42)


def test_get_all_order_intents_handles_null_atr():
    with tempfile.TemporaryDirectory() as td:
        db_path = Path(td) / "state2.db"
        # use SchemaManager + StateStore to create full schema
        SchemaManager.ensure_schema(str(db_path))
        store = StateStore(str(db_path))

        # insert one with atr None
        store.save_order_intent(
            client_order_id="no-atr",
            symbol="MSFT",
            side="sell",
            qty=2.0,
            status="new",
            atr=None,
        )

        intents = store.get_all_order_intents()
        assert any(i["client_order_id"] == "no-atr" and i["atr"] is None for i in intents)
