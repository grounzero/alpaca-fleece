"""Tests for exit signal deduplication (Fix 1.2)."""

from unittest.mock import MagicMock

import pytest

from src.exit_manager import ExitManager
from src.position_tracker import PositionTracker


@pytest.fixture
def position_tracker(tmp_path):
    """Create position tracker with test database."""
    from src.broker import Broker
    from src.schema_manager import SchemaManager
    from src.state_store import StateStore

    db_path = str(tmp_path / "test.db")
    SchemaManager.ensure_schema(db_path)
    state_store = StateStore(db_path)
    broker = MagicMock(spec=Broker)

    tracker = PositionTracker(broker, state_store)
    return tracker


@pytest.fixture
def exit_manager(position_tracker, tmp_path):
    """Create exit manager for testing."""
    from src.broker import Broker
    from src.data_handler import DataHandler
    from src.event_bus import EventBus
    from src.schema_manager import SchemaManager
    from src.state_store import StateStore

    db_path = str(tmp_path / "test.db")
    SchemaManager.ensure_schema(db_path)
    state_store = StateStore(db_path)
    broker = MagicMock(spec=Broker)
    event_bus = EventBus()
    data_handler = MagicMock(spec=DataHandler)

    manager = ExitManager(
        broker=broker,
        position_tracker=position_tracker,
        event_bus=event_bus,
        state_store=state_store,
        data_handler=data_handler,
        stop_loss_pct=0.01,
        profit_target_pct=0.02,
    )
    return manager


@pytest.mark.asyncio
async def test_first_exit_signal_sets_pending_exit(exit_manager, position_tracker):
    """First exit signal sets pending_exit=True on position."""
    # Setup position
    position = position_tracker.start_tracking(
        symbol="AAPL",
        fill_price=100.0,
        qty=10,
        side="long",
    )

    assert position.pending_exit is False

    # Market is open
    exit_manager.broker.get_clock.return_value = {"is_open": True}

    # Price triggers stop loss
    exit_manager.data_handler.get_snapshot.return_value = {
        "last_price": 99.0,
        "bid": 99.0,
        "ask": 99.1,
    }

    await exit_manager.event_bus.start()
    signals = await exit_manager.check_positions()
    await exit_manager.event_bus.stop()

    assert len(signals) == 1
    assert signals[0].reason == "stop_loss"

    # Check that pending_exit is now True
    updated_position = position_tracker.get_position("AAPL")
    assert updated_position.pending_exit is True


@pytest.mark.asyncio
async def test_second_check_skips_position_with_pending_exit(exit_manager, position_tracker):
    """Second check_positions() skips position with pending_exit=True."""
    # Setup position
    position = position_tracker.start_tracking(
        symbol="AAPL",
        fill_price=100.0,
        qty=10,
        side="long",
    )

    # Manually set pending_exit to True
    position.pending_exit = True
    position_tracker._persist_position(position)

    # Market is open
    exit_manager.broker.get_clock.return_value = {"is_open": True}

    # Price still triggers stop loss, but should be skipped
    exit_manager.data_handler.get_snapshot.return_value = {
        "last_price": 99.0,
        "bid": 99.0,
        "ask": 99.1,
    }

    await exit_manager.event_bus.start()
    signals = await exit_manager.check_positions()
    await exit_manager.event_bus.stop()

    # No signal should be generated because position has pending_exit
    assert len(signals) == 0


@pytest.mark.asyncio
async def test_rapid_checks_generate_only_one_signal(exit_manager, position_tracker):
    """Two rapid check_positions() calls generate only one signal."""
    # Setup position
    position_tracker.start_tracking(
        symbol="AAPL",
        fill_price=100.0,
        qty=10,
        side="long",
    )

    # Market is open
    exit_manager.broker.get_clock.return_value = {"is_open": True}

    # Price triggers stop loss
    exit_manager.data_handler.get_snapshot.return_value = {
        "last_price": 99.0,
        "bid": 99.0,
        "ask": 99.1,
    }

    await exit_manager.event_bus.start()

    # First check - should generate signal
    signals1 = await exit_manager.check_positions()
    assert len(signals1) == 1

    # Second check immediately after - should not generate signal (pending_exit=True)
    signals2 = await exit_manager.check_positions()
    assert len(signals2) == 0

    await exit_manager.event_bus.stop()


@pytest.mark.asyncio
async def test_pending_exit_survives_restart(position_tracker, exit_manager, tmp_path):
    """pending_exit flag persists to SQLite and survives restart."""

    # Use same position tracker instance
    position = position_tracker.start_tracking(
        symbol="AAPL",
        fill_price=100.0,
        qty=10,
        side="long",
    )

    # Set pending_exit and persist
    position.pending_exit = True
    position_tracker._persist_position(position)

    # Load positions from SQLite (simulating restart)
    loaded_positions = position_tracker.load_persisted_positions()

    assert len(loaded_positions) == 1
    assert loaded_positions[0].symbol == "AAPL"
    assert loaded_positions[0].pending_exit is True
