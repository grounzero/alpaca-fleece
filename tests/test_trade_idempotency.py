import sqlite3
from datetime import datetime, timezone

from src.data.order_updates import OrderUpdatesHandler
from src.event_bus import EventBus, OrderUpdateEvent
from src.state_store import StateStore


def test_record_trade_idempotent(tmp_path):
    db_path = str(tmp_path / "state.db")
    store = StateStore(db_path)
    eb = EventBus()
    handler = OrderUpdatesHandler(store, eb)

    # First record arrives without a fill_id (e.g., streaming delivered earlier)
    event1 = OrderUpdateEvent(
        order_id="order-1",
        client_order_id="client-1",
        symbol="TEST",
        side="buy",
        status="filled",
        filled_qty=1.0,
        avg_fill_price=123.45,
        fill_id=None,
        timestamp=datetime.now(timezone.utc),
    )

    # Later the same update is delivered with a fill_id; upsert should backfill it
    event2 = OrderUpdateEvent(
        order_id="order-1",
        client_order_id="client-1",
        symbol="TEST",
        side="buy",
        status="filled",
        filled_qty=1.0,
        avg_fill_price=123.45,
        fill_id="fill-xyz",
        timestamp=datetime.now(timezone.utc),
    )

    # Record both events (simulate out-of-order / duplicate deliveries)
    handler.record_filled_trade(event1)
    handler.record_filled_trade(event2)

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()

    cur.execute(
        "SELECT COUNT(*), fill_id FROM trades WHERE order_id = ? AND client_order_id = ?",
        ("order-1", "client-1"),
    )
    row = cur.fetchone()
    conn.close()

    assert row is not None
    count, fill_id = row
    assert count == 1
    assert fill_id == "fill-xyz"
