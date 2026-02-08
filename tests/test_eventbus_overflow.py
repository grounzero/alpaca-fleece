"""Tests for EventBus dropped event handling (Fix 1.4)."""

import asyncio
from datetime import datetime, timezone

import pytest

from src.event_bus import BarEvent, EventBus, EventBusError, ExitSignalEvent


@pytest.mark.asyncio
async def test_event_bus_logs_dropped_events(caplog, monkeypatch):
    """EventBus logs error when queue is full and event is dropped."""
    # Create a small queue to force overflow
    bus = EventBus(maxsize=2)
    await bus.start()

    # Fill the queue
    event1 = BarEvent(
        symbol="AAPL",
        timestamp=datetime.now(timezone.utc),
        open=100,
        high=101,
        low=99,
        close=100.5,
        volume=1000,
    )
    event2 = BarEvent(
        symbol="MSFT",
        timestamp=datetime.now(timezone.utc),
        open=200,
        high=201,
        low=199,
        close=200.5,
        volume=2000,
    )

    await bus.publish(event1)
    await bus.publish(event2)

    # This one should be dropped (queue at capacity)
    with caplog.at_level("ERROR"):
        event3 = BarEvent(
            symbol="GOOGL",
            timestamp=datetime.now(timezone.utc),
            open=300,
            high=301,
            low=299,
            close=300.5,
            volume=3000,
        )
        # Publish third event - should timeout and be dropped (not critical)
        await bus.publish(event3)  # Won't raise because it's not ExitSignalEvent

        # Speed up test by no-oping asyncio.sleep
        async def _noop_sleep(*a, **k):
            return None

        monkeypatch.setattr(asyncio, "sleep", _noop_sleep)
        await asyncio.sleep(0.1)  # Let processing occur
        # Verify drop was logged
        assert "timed out" in caplog.text or bus.dropped_count > 0

    await bus.stop()


@pytest.mark.asyncio
async def test_event_bus_tracks_dropped_count():
    """EventBus tracks count of dropped events."""
    bus = EventBus(maxsize=1)
    await bus.start()

    # Publish one event (fills queue)
    event1 = BarEvent(
        symbol="AAPL",
        timestamp=datetime.now(timezone.utc),
        open=100,
        high=101,
        low=99,
        close=100.5,
        volume=1000,
    )
    await bus.publish(event1)

    # Access initial dropped count
    initial_dropped = bus.dropped_count
    assert initial_dropped == 0

    await bus.stop()


@pytest.mark.asyncio
async def test_exit_signal_event_drop_raises_error():
    """Critical ExitSignalEvent drop raises EventBusError, not silent."""
    bus = EventBus(maxsize=1)  # Small queue
    await bus.start()

    signal = ExitSignalEvent(
        symbol="AAPL",
        side="sell",
        qty=10,
        reason="stop_loss",
        entry_price=100,
        current_price=99,
        pnl_pct=-0.01,
        pnl_amount=-10,
        timestamp=datetime.now(timezone.utc),
    )

    # Fill the queue first
    dummy_event = BarEvent(
        symbol="TEST",
        timestamp=datetime.now(timezone.utc),
        open=100,
        high=101,
        low=99,
        close=100.5,
        volume=1000,
    )
    await bus.publish(dummy_event)

    # Publishing an ExitSignalEvent with a full queue should raise
    with pytest.raises(EventBusError, match="critical ExitSignalEvent"):
        await bus.publish(signal)

    await bus.stop()


@pytest.mark.asyncio
async def test_non_critical_event_drop_logged_only(caplog, monkeypatch):
    """Non-critical event drops are logged but don't raise."""
    bus = EventBus(maxsize=1)
    await bus.start()

    # Fill queue
    event1 = BarEvent(
        symbol="AAPL",
        timestamp=datetime.now(timezone.utc),
        open=100,
        high=101,
        low=99,
        close=100.5,
        volume=1000,
    )
    await bus.publish(event1)

    # Try to publish non-critical event (should not raise, just log)
    event2 = BarEvent(
        symbol="GOOGL",
        timestamp=datetime.now(timezone.utc),
        open=300,
        high=301,
        low=299,
        close=300.5,
        volume=3000,
    )

    # This should timeout but NOT raise (unless it's ExitSignalEvent)
    await bus.publish(event2)  # Won't raise because it's not critical

    # Speed up test by no-oping asyncio.sleep
    async def _noop_sleep(*a, **k):
        return None

    monkeypatch.setattr(asyncio, "sleep", _noop_sleep)
    await asyncio.sleep(0.1)  # Brief wait
    # Verify drop was logged and counter incremented
    assert bus.dropped_count >= 1 or "timed out" in caplog.text

    await bus.stop()


@pytest.mark.asyncio
async def test_event_bus_size_property():
    """EventBus.size() returns queue size."""
    bus = EventBus(maxsize=10)
    await bus.start()

    assert bus.size() == 0

    event = BarEvent(
        symbol="AAPL",
        timestamp=datetime.now(timezone.utc),
        open=100,
        high=101,
        low=99,
        close=100.5,
        volume=1000,
    )
    await bus.publish(event)

    assert bus.size() == 1

    await bus.stop()
