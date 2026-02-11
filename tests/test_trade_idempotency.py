import sqlite3
from datetime import datetime, timezone

from src.state_store import StateStore
from src.event_bus import OrderUpdateEvent, EventBus
from src.data.order_updates import OrderUpdatesHandler


def test_record_trade_idempotent(tmp_path):
    db_path = str(tmp_path / "state.db")
    store = StateStore(db_path)
    eb = EventBus()
    handler = OrderUpdatesHandler(store, eb)

    event = OrderUpdateEvent(
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

    # Record twice (simulate duplicate delivery)
    handler._record_trade(event)
    handler._record_trade(event)

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.execute("SELECT COUNT(*) FROM trades WHERE order_id = ? AND client_order_id = ?", ("order-1", "client-1"))
    count = cur.fetchone()[0]
    conn.close()

    assert count == 1
