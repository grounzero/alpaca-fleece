"""Tests for OrderUpdatesHandler ensuring filled_qty is handled as Optional."""

import sqlite3
from datetime import datetime, timezone
from types import SimpleNamespace

import pytest

from src.data.order_updates import OrderUpdatesHandler
from src.state_store import StateStore


class DummyEventBus:
    def __init__(self):
        self.published = []

    async def publish(self, event):
        self.published.append(event)


@pytest.mark.asyncio
async def test_preserve_existing_filled_qty_when_missing(tmp_path):
    db_path = str(tmp_path / "test.db")
    store = StateStore(db_path)
    bus = DummyEventBus()
    handler = OrderUpdatesHandler(store, bus)

    # Insert order intent with existing filled_qty = 5
    now = datetime.now(timezone.utc).isoformat()
    # Use a context manager to ensure the connection is closed reliably.
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order", "AAPL", "buy", 100, "submitted", 5, "alpaca-1", now, now),
        )

    # Create raw update missing filled_qty (attribute present but None)
    raw = SimpleNamespace(
        order=SimpleNamespace(
            id="alpaca-1",
            client_order_id="test-order",
            symbol="AAPL",
            side="buy",
            status="partially_filled",
            filled_qty=None,
            filled_avg_price=None,
        ),
        at=datetime.now(timezone.utc),
    )

    await handler.on_order_update(raw)

    # Verify DB still has filled_qty=5
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT filled_qty FROM order_intents WHERE client_order_id = ?", ("test-order",)
        )
        row = cur.fetchone()

    assert row[0] == 5


@pytest.mark.asyncio
async def test_apply_partial_fill_updates_filled_qty(tmp_path):
    db_path = str(tmp_path / "test2.db")
    store = StateStore(db_path)
    bus = DummyEventBus()
    handler = OrderUpdatesHandler(store, bus)

    # Insert order intent with existing filled_qty = 2
    now = datetime.now(timezone.utc).isoformat()
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order-2", "AAPL", "buy", 100, "submitted", 2, "alpaca-2", now, now),
        )

    # Create raw update with partial fill filled_qty=6
    raw = SimpleNamespace(
        order=SimpleNamespace(
            id="alpaca-2",
            client_order_id="test-order-2",
            symbol="AAPL",
            side="buy",
            status="partially_filled",
            filled_qty="6",
            filled_avg_price="120.5",
        ),
        at=datetime.now(timezone.utc),
    )

    await handler.on_order_update(raw)

    # Verify DB now has filled_qty=6
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT filled_qty, filled_avg_price FROM order_intents WHERE client_order_id = ?",
            ("test-order-2",),
        )
        row = cur.fetchone()

    assert float(row[0]) == 6.0
    assert float(row[1]) == 120.5


@pytest.mark.asyncio
async def test_preserve_existing_filled_qty_when_attribute_absent(tmp_path):
    """If the incoming order object lacks a `filled_qty` attribute entirely,
    parsing should treat it as missing (None) and preserve the DB value.
    """
    db_path = str(tmp_path / "test-absent.db")
    store = StateStore(db_path)
    bus = DummyEventBus()
    handler = OrderUpdatesHandler(store, bus)

    now = datetime.now(timezone.utc).isoformat()
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order-3", "AAPL", "buy", 100, "submitted", 7, "alpaca-3", now, now),
        )

    # Build a raw update where the `order` object does NOT have `filled_qty`
    # A plain SimpleNamespace without the attribute exercises the absent case.
    order_obj = SimpleNamespace(
        id="alpaca-3",
        client_order_id="test-order-3",
        symbol="AAPL",
        side="buy",
        status="partially_filled",
    )

    raw = SimpleNamespace(order=order_obj, at=datetime.now(timezone.utc))

    await handler.on_order_update(raw)

    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT filled_qty FROM order_intents WHERE client_order_id = ?",
            ("test-order-3",),
        )
        row = cur.fetchone()

    assert row[0] == 7


@pytest.mark.asyncio
async def test_order_dict_parsing_preserves_missing_filled_qty(tmp_path):
    """When the incoming order is a dict (mapping), missing keys should be
    treated as None and not overwrite existing DB values.
    """
    db_path = str(tmp_path / "test-dict.db")
    store = StateStore(db_path)
    bus = DummyEventBus()
    handler = OrderUpdatesHandler(store, bus)

    now = datetime.now(timezone.utc).isoformat()
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order-4", "AAPL", "buy", 100, "submitted", 3, "alpaca-4", now, now),
        )

    # Represent the incoming order as a dict with no `filled_qty` key
    raw = SimpleNamespace(
        order={
            "id": "alpaca-4",
            "client_order_id": "test-order-4",
            "symbol": "AAPL",
            "side": "buy",
            "status": "partially_filled",
            # intentionally no 'filled_qty' key
        },
        at=datetime.now(timezone.utc),
    )

    await handler.on_order_update(raw)

    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT filled_qty FROM order_intents WHERE client_order_id = ?",
            ("test-order-4",),
        )
        row = cur.fetchone()

    assert row[0] == 3
