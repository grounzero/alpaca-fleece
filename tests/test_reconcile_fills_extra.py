import sqlite3
from datetime import datetime, timezone

import pytest

from unittest.mock import AsyncMock, MagicMock

from src.schema_manager import SchemaManager
from src.state_store import StateStore
from src.reconciliation import reconcile_fills


def _init_db(db_path: str) -> None:
    SchemaManager.ensure_schema(db_path)


def _insert_order_intent(db_path: str, client_order_id: str, alpaca_order_id: str, status: str, filled_qty: float = 0.0, symbol: str = "AAPL", side: str = "buy", qty: float = 100.0) -> None:
    now = datetime.now(timezone.utc).isoformat()
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            "INSERT OR REPLACE INTO order_intents (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, created_at_utc, updated_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, now, now),
        )
        conn.commit()


@pytest.mark.asyncio
async def test_reconcile_fills_synthesises_on_broker_drift(tmp_path):
    db_path = str(tmp_path / "recon.db")
    _init_db(db_path)
    store = StateStore(db_path)

    # DB thinks 10 filled
    _insert_order_intent(db_path, "c-1", "a-1", status="partially_filled", filled_qty=10)

    # Broker reports 25 filled
    mock_broker = MagicMock()
    mock_broker.get_open_orders = AsyncMock(
        return_value=[
            {
                "id": "a-1",
                "client_order_id": "c-1",
                "symbol": "AAPL",
                "side": "buy",
                "status": "partially_filled",
                "filled_qty": 25,
                "filled_avg_price": 150.0,
            }
        ]
    )

    on_order_update = AsyncMock()

    count = await reconcile_fills(broker=mock_broker, state_store=store, on_order_update=on_order_update)

    assert count == 1
    assert on_order_update.await_count == 1


@pytest.mark.asyncio
async def test_reconcile_fills_handles_broker_api_failure(tmp_path):
    db_path = str(tmp_path / "recon2.db")
    _init_db(db_path)
    store = StateStore(db_path)

    _insert_order_intent(db_path, "c-1", "a-1", status="partially_filled", filled_qty=10)

    mock_broker = MagicMock()
    mock_broker.get_open_orders = AsyncMock(side_effect=Exception("Broker API error"))

    on_order_update = AsyncMock()

    count = await reconcile_fills(broker=mock_broker, state_store=store, on_order_update=on_order_update)

    assert count == 0
    assert on_order_update.await_count == 0


@pytest.mark.asyncio
async def test_reconcile_fills_fetches_individual_order_if_missing_from_open(tmp_path):
    db_path = str(tmp_path / "recon3.db")
    _init_db(db_path)
    store = StateStore(db_path)

    _insert_order_intent(db_path, "c-2", "a-2", status="partially_filled", filled_qty=5)

    mock_broker = MagicMock()
    # No open orders returned
    mock_broker.get_open_orders = AsyncMock(return_value=[])
    # But broker.get_order returns the authoritative order with higher filled_qty
    mock_broker.get_order = AsyncMock(return_value={
        "id": "a-2",
        "client_order_id": "c-2",
        "symbol": "AAPL",
        "side": "buy",
        "status": "partially_filled",
        "filled_qty": 15,
        "filled_avg_price": 150.0,
    })

    on_order_update = AsyncMock()

    count = await reconcile_fills(broker=mock_broker, state_store=store, on_order_update=on_order_update)

    assert count == 1
    assert on_order_update.await_count == 1


@pytest.mark.asyncio
async def test_reconcile_fills_skips_terminal_or_missing_orders(tmp_path):
    db_path = str(tmp_path / "recon4.db")
    _init_db(db_path)
    store = StateStore(db_path)

    # Terminal order in DB should not be reconciled
    _insert_order_intent(db_path, "c-term", "a-term", status="filled", filled_qty=100)

    mock_broker = MagicMock()
    mock_broker.get_open_orders = AsyncMock(return_value=[])

    on_order_update = AsyncMock()

    count = await reconcile_fills(broker=mock_broker, state_store=store, on_order_update=on_order_update)

    assert count == 0
    assert on_order_update.await_count == 0
