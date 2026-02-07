"""Tests for event bus."""

import pytest
from datetime import datetime, timezone

from src.event_bus import EventBus, BarEvent, SignalEvent


@pytest.mark.asyncio
async def test_event_bus_publish_subscribe():
    """Event bus publishes and subscribes events."""
    bus = EventBus()
    await bus.start()
    
    # Create and publish event
    event = BarEvent(
        symbol="AAPL",
        timestamp=datetime.now(timezone.utc),
        open=100.0,
        high=101.0,
        low=99.0,
        close=100.5,
        volume=1000,
    )
    
    await bus.publish(event)
    
    # Subscribe and receive
    received = await bus.subscribe()
    assert received == event
    
    await bus.stop()


@pytest.mark.asyncio
async def test_event_bus_multiple_events():
    """Event bus handles multiple events in sequence."""
    bus = EventBus()
    await bus.start()
    
    # Publish multiple events
    events = [
        BarEvent(
            symbol="AAPL",
            timestamp=datetime.now(timezone.utc),
            open=100.0,
            high=101.0,
            low=99.0,
            close=100.5,
            volume=1000,
        ),
        SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={},
        ),
    ]
    
    for event in events:
        await bus.publish(event)
    
    # Receive in order
    received1 = await bus.subscribe()
    assert isinstance(received1, BarEvent)
    
    received2 = await bus.subscribe()
    assert isinstance(received2, SignalEvent)
    
    await bus.stop()


@pytest.mark.asyncio
async def test_event_bus_size():
    """Event bus tracks queue size."""
    bus = EventBus()
    await bus.start()
    
    assert bus.size() == 0
    
    event = BarEvent(
        symbol="AAPL",
        timestamp=datetime.now(timezone.utc),
        open=100.0,
        high=101.0,
        low=99.0,
        close=100.5,
        volume=1000,
    )
    
    await bus.publish(event)
    assert bus.size() == 1
    
    await bus.subscribe()
    assert bus.size() == 0
    
    await bus.stop()
