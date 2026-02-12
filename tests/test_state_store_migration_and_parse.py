import sqlite3
from decimal import Decimal

import pytest

from src.schema_manager import SchemaManager
from src.utils import parse_optional_float


def test_migration_adds_atr_column(tmp_path):
    db_file = tmp_path / "test_state.db"

    # Create an older schema without the 'atr' column
    with sqlite3.connect(db_file) as conn:
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

    # SchemaManager should add the missing 'atr' column
    SchemaManager.ensure_schema(str(db_file))

    with sqlite3.connect(db_file) as conn:
        cur = conn.cursor()
        cur.execute("PRAGMA table_info(order_intents)")
        cols = [r[1] for r in cur.fetchall()]

    assert "atr" in cols


@pytest.mark.parametrize(
    "val,expected",
    [
        ("NaN", None),
        ("inf", None),
        (Decimal("NaN"), None),
        (Decimal("1.234"), 1.234),
        ("2.5", 2.5),
    ],
)
def test_parse_optional_float_clamps_nan_inf(val, expected):
    res = parse_optional_float(val)
    if expected is None:
        assert res is None
    else:
        assert isinstance(res, float)
        assert abs(res - expected) < 1e-9
