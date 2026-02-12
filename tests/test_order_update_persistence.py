import sqlite3
from datetime import datetime, timezone

import pytest

from src.state_store import StateStore


def test_missing_filled_fields_do_not_overwrite(tmp_path):
    db_path = tmp_path / "test_store.db"
    ss = StateStore(str(db_path))

    client_id = "test_client_1"

    # Persist initial intent
    ss.save_order_intent(
        client_order_id=client_id,
        symbol="AAPL",
        side="buy",
        qty=1.0,
        status="new",
    )

    # Set initial filled values
    ss.update_order_intent(
        client_order_id=client_id,
        status="submitted",
        filled_qty=5.0,
        alpaca_order_id="alpaca-1",
        filled_avg_price=10.0,
    )

    oi = ss.get_order_intent(client_id)
    assert oi is not None
    assert oi["filled_qty"] == 5.0
    assert oi["filled_avg_price"] == 10.0

    # Apply an update that omits filled fields (pass None)
    ss.update_order_intent(
        client_order_id=client_id,
        status="accepted",
        filled_qty=None,
        alpaca_order_id="alpaca-1",
        filled_avg_price=None,
    )

    oi2 = ss.get_order_intent(client_id)
    assert oi2 is not None
    # Values must be preserved
    assert oi2["filled_qty"] == 5.0
    assert oi2["filled_avg_price"] == 10.0

    # Now apply an update with new numeric values
    ss.update_order_intent(
        client_order_id=client_id,
        status="partially_filled",
        filled_qty=7.5,
        alpaca_order_id="alpaca-1",
        filled_avg_price=15.25,
    )

    oi3 = ss.get_order_intent(client_id)
    assert oi3 is not None
    assert oi3["filled_qty"] == 7.5
    assert oi3["filled_avg_price"] == 15.25
