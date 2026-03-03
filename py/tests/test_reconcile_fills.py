from unittest.mock import AsyncMock, MagicMock

import pytest

from src.reconciliation import reconcile_fills
from src.schema_manager import SchemaManager
from src.state_store import StateStore


@pytest.fixture
def setup_db(tmp_path):
    db_path = str(tmp_path / "state.db")
    SchemaManager.ensure_schema(db_path)
    store = StateStore(db_path)
    return db_path, store


@pytest.mark.asyncio
async def test_reconcile_fills_synthesises_event_when_broker_higher(setup_db):
    db_path, store = setup_db

    # Insert an order intent with filled_qty=0
    store.save_order_intent("client-1", "TEST", "buy", 1.0, status="submitted")
    # Ensure the DB row contains an alpaca_order_id so it is considered
    # for reconciliation (reconciler filters on presence of alpaca_order_id).
    store.update_order_intent(
        "client-1", status="submitted", filled_qty=None, alpaca_order_id="alpaca-1"
    )

    # Create a fake broker that reports a higher filled_qty for the order
    broker = MagicMock()
    broker.get_open_orders = AsyncMock(
        return_value=[
            {
                "id": "alpaca-1",
                "client_order_id": "client-1",
                "symbol": "TEST",
                "side": "buy",
                "status": "accepted",
                "filled_qty": "1",
                "filled_avg_price": "100",
            }
        ]
    )

    # Spy handler: capture the synthesised event passed to on_order_update
    seen = []

    async def on_order_update(evt):
        seen.append(evt)

    count = await reconcile_fills(broker=broker, state_store=store, on_order_update=on_order_update)

    assert count == 1
    assert len(seen) == 1
    evt = seen[0]
    # event wrapper from stream_polling has .order with expected fields
    assert getattr(evt, "order").client_order_id == "client-1"
    assert float(getattr(evt, "order").filled_qty) == 1.0


@pytest.mark.asyncio
async def test_reconcile_fills_fetch_order_when_missing_and_handles_failure(setup_db):
    db_path, store = setup_db

    store.save_order_intent("client-2", "TEST2", "sell", 2.0, status="submitted")
    store.update_order_intent(
        "client-2", status="submitted", filled_qty=None, alpaca_order_id="alpaca-2"
    )

    # Broker returns no open orders; get_order will be attempted and raise
    broker = MagicMock()
    broker.get_open_orders = AsyncMock(return_value=[])

    async def bad_get_order(id_):
        raise RuntimeError("broker error")

    broker.get_order = AsyncMock(side_effect=bad_get_order)

    seen = []

    async def on_order_update(evt):
        seen.append(evt)

    # Should not raise despite broker.get_order raising
    count = await reconcile_fills(broker=broker, state_store=store, on_order_update=on_order_update)
    assert count == 0
    assert seen == []


@pytest.mark.asyncio
async def test_reconcile_fills_handles_broker_api_failure(setup_db):
    db_path, store = setup_db

    # Save an order so non_terminal is non-empty
    store.save_order_intent("client-3", "TEST3", "buy", 1.0, status="submitted")
    store.update_order_intent(
        "client-3", status="submitted", filled_qty=None, alpaca_order_id="alpaca-3"
    )

    broker = MagicMock()
    broker.get_open_orders = AsyncMock(side_effect=RuntimeError("API down"))

    # Should catch exception and return 0
    count = await reconcile_fills(broker=broker, state_store=store, on_order_update=None)
    assert count == 0
