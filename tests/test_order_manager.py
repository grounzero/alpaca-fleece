"""Tests for order manager."""

import pytest
from datetime import datetime, timezone

from src.order_manager import OrderManager, OrderManagerError
from src.event_bus import SignalEvent


def test_order_manager_generates_deterministic_client_order_id(
    state_store, event_bus, mock_broker, config
):
    """Order manager generates consistent client_order_id for same inputs."""
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
        timeframe="1Min",
    )

    ts = datetime(2024, 1, 1, 10, 0, 0, tzinfo=timezone.utc)

    # Generate order ID twice with same inputs
    id1 = order_mgr._generate_client_order_id("AAPL", ts, "buy")
    id2 = order_mgr._generate_client_order_id("AAPL", ts, "buy")

    # Should be identical
    assert id1 == id2

    # Different inputs should produce different IDs
    id3 = order_mgr._generate_client_order_id("MSFT", ts, "buy")
    assert id1 != id3


@pytest.mark.asyncio
async def test_order_manager_prevents_duplicate_orders(state_store, event_bus, mock_broker, config):
    """Order manager prevents duplicate orders."""
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Mock broker to succeed
    mock_broker.submit_order.return_value = {
        "id": "alpaca-order-123",
        "client_order_id": order_mgr._generate_client_order_id("AAPL", signal.timestamp, "buy"),
        "symbol": "AAPL",
        "status": "submitted",
    }

    # Submit first order
    result1 = await order_mgr.submit_order(signal, qty=10.0)
    assert result1 is True

    # Submit identical signal again (should be prevented)
    result2 = await order_mgr.submit_order(signal, qty=10.0)
    assert result2 is False  # Duplicate prevented


@pytest.mark.asyncio
async def test_order_manager_persists_intent_before_submission(
    state_store, event_bus, mock_broker, config
):
    """Order manager persists order intent BEFORE submitting (crash safety)."""
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Mock broker to fail on submission
    mock_broker.submit_order.side_effect = Exception("Network error")

    # Try to submit (will fail)
    with pytest.raises(OrderManagerError):
        await order_mgr.submit_order(signal, qty=10.0)

    # But order intent should still be persisted (crash safety)
    client_id = order_mgr._generate_client_order_id("AAPL", signal.timestamp, "buy")
    intent = state_store.get_order_intent(client_id)
    assert intent is not None
    assert intent["symbol"] == "AAPL"


@pytest.mark.asyncio
async def test_order_manager_publishes_to_event_bus(state_store, event_bus, mock_broker, config):
    """Order manager publishes OrderIntentEvent to EventBus."""
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Mock broker to succeed
    mock_broker.submit_order.return_value = {
        "id": "alpaca-order-123",
        "client_order_id": order_mgr._generate_client_order_id("AAPL", signal.timestamp, "buy"),
        "symbol": "AAPL",
        "status": "submitted",
    }

    # Submit order
    await order_mgr.submit_order(signal, qty=10.0)

    # Event should be published
    assert event_bus.size() > 0  # Event in queue


@pytest.mark.asyncio
async def test_order_manager_increments_circuit_breaker_on_failure(
    state_store, event_bus, mock_broker, config
):
    """Order manager increments circuit breaker on submission failure."""
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    # Mock broker to fail
    mock_broker.submit_order.side_effect = Exception("Broker error")

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime(2024, 1, 1, 10, 0, 0, tzinfo=timezone.utc),
        metadata={},
    )

    # Submit order (will fail)
    with pytest.raises(OrderManagerError):
        await order_mgr.submit_order(signal, qty=10.0)

    # Circuit breaker should be incremented (Win #3: now persisted)
    cb_failures = state_store.get_circuit_breaker_count()  # Win #3: use new method
    assert cb_failures == 1

    # Submit different signal again (will fail again)
    signal2 = SignalEvent(
        symbol="MSFT",
        signal_type="SELL",
        timestamp=datetime(2024, 1, 1, 10, 1, 0, tzinfo=timezone.utc),
        metadata={},
    )
    with pytest.raises(OrderManagerError):
        await order_mgr.submit_order(signal2, qty=10.0)

    cb_failures = state_store.get_circuit_breaker_count()  # Win #3: use new method
    assert cb_failures == 2


@pytest.mark.asyncio
async def test_order_manager_trips_circuit_breaker_at_5_failures(
    state_store, event_bus, mock_broker, config
):
    """Circuit breaker trips after 5 consecutive failures."""
    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    # Mock broker to fail
    mock_broker.submit_order.side_effect = Exception("Broker error")

    # Submit 5 different signals (will fail each time)
    for i in range(5):
        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime(2024, 1, 1, 10, i, 0, tzinfo=timezone.utc),
            metadata={},
        )
        with pytest.raises(OrderManagerError):
            await order_mgr.submit_order(signal, qty=10.0)

    # Circuit breaker should be tripped
    cb_state = state_store.get_state("circuit_breaker_state")
    assert cb_state == "tripped"
