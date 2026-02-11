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

    # Create raw update missing filled_qty
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
        cur.execute("SELECT filled_qty FROM order_intents WHERE client_order_id = ?", ("test-order",))
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
