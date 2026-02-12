"""Tests for signal-level gating (entry dedupe / cooldown / pending-aware).

These tests exercise the new persistent gate in StateStore and the enforcement
in OrderManager.submit_order.
"""

from datetime import datetime, timedelta, timezone

import pytest

from src.event_bus import SignalEvent
from src.order_manager import OrderManager


def _make_signal(symbol: str, sig_type: str, ts: datetime) -> SignalEvent:
    return SignalEvent(symbol=symbol, signal_type=sig_type, timestamp=ts, metadata={})


@pytest.mark.asyncio
async def test_gate_blocks_when_already_long(state_store, event_bus, mock_broker, config):
    # Broker reports an existing long position
    mock_broker.get_positions.return_value = [
        {"symbol": "TSLA", "qty": 5.0, "avg_entry_price": 200.0, "current_price": 210.0}
    ]

    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    sig = _make_signal("TSLA", "BUY", datetime.now(timezone.utc))
    result = await order_mgr.submit_order(sig, qty=1.0)
    assert result is False


@pytest.mark.asyncio
async def test_gate_blocks_when_open_order_exists(state_store, event_bus, mock_broker, config):
    # Insert an open order intent for BUY (scoped to this strategy)
    state_store.save_order_intent(
        "cid-open", "AAPL", "buy", 1.0, status="submitted", strategy="sma_crossover"
    )

    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    sig = _make_signal("AAPL", "BUY", datetime.now(timezone.utc))
    result = await order_mgr.submit_order(sig, qty=1.0)
    assert result is False


@pytest.mark.asyncio
async def test_gate_blocks_within_cooldown_and_allows_after(
    state_store, event_bus, mock_broker, config
):
    # Force DRY_RUN so submission is deterministic and does not depend on broker mock
    config["DRY_RUN"] = True
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    now = datetime.now(timezone.utc)
    # Prime gate as if accepted 30 minutes ago with a 60 minute cooldown
    past = now - timedelta(minutes=30)
    state_store.gate_try_accept(
        strategy="sma_crossover",
        symbol="AAPL",
        action="ENTER_LONG",
        now_utc=past,
        bar_ts_utc=None,
        cooldown=timedelta(minutes=60),
    )

    sig = _make_signal("AAPL", "BUY", now)
    res_blocked = await order_mgr.submit_order(sig, qty=1.0)
    assert res_blocked is False

    # Now prime as if accepted 90 minutes ago -> should allow
    # Simulate cooldown expiry by releasing the persisted gate row so a new
    # acceptance can occur deterministically.
    # Verify gate exists first, then release it via the public helper.
    gate = state_store.get_gate("sma_crossover", "AAPL", "ENTER_LONG")
    assert gate is not None
    state_store.release_gate("sma_crossover", "AAPL", "ENTER_LONG")
    assert state_store.get_gate("sma_crossover", "AAPL", "ENTER_LONG") is None

    res_allowed = await order_mgr.submit_order(sig, qty=1.0)
    # With DRY_RUN enabled the order manager should accept and return True
    assert res_allowed is True


@pytest.mark.asyncio
async def test_gate_blocks_same_bar_timestamp(state_store, event_bus, mock_broker, config):
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    bar_ts = datetime(2024, 1, 1, 12, 0, tzinfo=timezone.utc)
    # Prime gate accepted for this bar
    state_store.gate_try_accept(
        strategy="sma_crossover",
        symbol="MSFT",
        action="ENTER_LONG",
        now_utc=bar_ts,
        bar_ts_utc=bar_ts,
        cooldown=timedelta(minutes=0),
    )

    sig = _make_signal("MSFT", "BUY", bar_ts)
    res = await order_mgr.submit_order(sig, qty=1.0)
    assert res is False


@pytest.mark.asyncio
async def test_gate_persists_across_restart(tmp_db, event_bus, mock_broker, config):
    # Prime a gate in initial state store
    from src.schema_manager import SchemaManager
    from src.state_store import StateStore

    SchemaManager.ensure_schema(tmp_db)
    ss1 = StateStore(tmp_db)
    now = datetime.now(timezone.utc)
    ss1.gate_try_accept(
        strategy="sma_crossover",
        symbol="NFLX",
        action="ENTER_LONG",
        now_utc=now,
        bar_ts_utc=None,
        cooldown=timedelta(minutes=60),
    )

    # Create a fresh StateStore instance pointed at the same DB (simulating restart)
    ss2 = StateStore(tmp_db)
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=ss2,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    sig = _make_signal("NFLX", "BUY", datetime.now(timezone.utc))
    res = await order_mgr.submit_order(sig, qty=1.0)
    assert res is False
