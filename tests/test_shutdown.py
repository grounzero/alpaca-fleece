import pytest

from src.order_manager import OrderManager
from src.state_store import StateStore
from src.schema_manager import SchemaManager


class DummyEventBus:
    async def publish(self, *args, **kwargs):
        return None


class BrokerMock:
    def __init__(self, positions, submit_side_behavior=None):
        # positions: list of dicts {symbol, qty}
        self._positions = positions
        self.submit_calls = []
        # submit_side_behavior: dict symbol->callable that may raise or return dict
        self.submit_side_behavior = submit_side_behavior or {}
        self.metrics = {}

    async def get_positions(self):
        return self._positions

    async def submit_order(self, *args, **kwargs):
        # capture call shape
        self.submit_calls.append({"args": args, "kwargs": kwargs})
        symbol = kwargs.get("symbol")
        behaviour = self.submit_side_behavior.get(symbol)
        if behaviour:
            return behaviour()
        return {"id": "ok-" + (symbol or "")}

    async def invalidate_cache(self, *args, **kwargs):
        return None


@pytest.mark.asyncio
async def test_shutdown_flatten_uses_correct_broker_signature_and_client_order_id(state_store, config):
    store = state_store
    positions = [{"symbol": "AAA", "qty": "10"}, {"symbol": "BBB", "qty": "-5"}]
    broker = BrokerMock(positions)
    om = OrderManager(
        broker=broker,
        state_store=store,
        event_bus=DummyEventBus(),
        config=config,
        strategy_name="teststrat",
    )

    await om.flatten_positions("sess-1")

    # Two submitted intents (or skipped if duplicate), ensure calls captured
    assert len(broker.submit_calls) == 2
    for call in broker.submit_calls:
        kw = call["kwargs"]
        assert "symbol" in kw and kw["symbol"] in ("AAA", "BBB")
        assert "side" in kw and kw["side"] in ("sell", "buy")
        assert "qty" in kw and float(kw["qty"]) > 0
        assert "client_order_id" in kw and kw["client_order_id"]
        assert "order_type" in kw and kw["order_type"]
        assert "time_in_force" in kw and kw["time_in_force"]


@pytest.mark.asyncio
async def test_shutdown_continues_on_single_symbol_flatten_failure_and_alerts(tmp_path, config):
    db = tmp_path / "state2.db"
    # Ensure canonical schema via SchemaManager to keep tests resilient to schema changes
    SchemaManager.ensure_schema(str(db))
    store = StateStore(str(db))

    # First symbol will raise on submit, second will succeed
    def raise_on_first():
        raise RuntimeError("submit failed")

    def succeed_second():
        return {"id": "ok-BBB"}

    positions = [{"symbol": "AAA", "qty": "10"}, {"symbol": "BBB", "qty": "-5"}]
    broker = BrokerMock(
        positions, submit_side_behavior={"AAA": raise_on_first, "BBB": succeed_second}
    )

    om = OrderManager(
        broker=broker,
        state_store=store,
        event_bus=DummyEventBus(),
        config=config,
        strategy_name="teststrat",
    )

    summary = await om.flatten_positions("sess-2")

    # First failed, second submitted
    assert any(f.get("symbol") == "AAA" for f in summary.get("failed", []))
    assert any(s.get("symbol") == "BBB" for s in summary.get("submitted", []))


@pytest.mark.asyncio
async def test_shutdown_idempotent_flatten_does_not_duplicate_orders_on_retry(tmp_path, config):
    db = tmp_path / "state3.db"
    SchemaManager.ensure_schema(str(db))
    store = StateStore(str(db))
    positions = [{"symbol": "CCC", "qty": "3"}]
    broker = BrokerMock(positions)
    om = OrderManager(
        broker=broker,
        state_store=store,
        event_bus=DummyEventBus(),
        config=config,
        strategy_name="teststrat",
    )

    # First run should submit one order
    await om.flatten_positions("sess-3")
    assert len(broker.submit_calls) == 1

    # Clear submit_calls and run again - should not resubmit duplicate
    broker.submit_calls.clear()
    await om.flatten_positions("sess-3")
    assert len(broker.submit_calls) == 0
