import asyncio
from datetime import datetime, timezone
from unittest.mock import MagicMock

import pytest

from src.exit_manager import ExitManager
from src.position_tracker import PositionData


@pytest.mark.asyncio
async def test_exit_manager_uses_public_upsert_only():
    # Create a PositionData that will trigger a profit-target exit
    position = PositionData(
        symbol="TEST",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=100.0,
    )

    # Mock PositionTracker with only public methods
    mock_tracker = MagicMock()
    mock_tracker.get_all_positions.return_value = [position]
    # update_current_price will be called; allow it
    mock_tracker.update_current_price.return_value = None
    # Ensure calculate_pnl returns a profit above the default profit_target (0.02)
    mock_tracker.calculate_pnl.return_value = (3.0, 0.03)
    # Track public method calls
    mock_tracker.upsert_position = MagicMock()
    mock_tracker.persist_position = MagicMock()

    # Minimal mocks for broker/data/event_bus/state_store
    mock_broker = MagicMock()
    mock_broker.get_clock.return_value = {"is_open": True}

    mock_data_handler = MagicMock()
    mock_data_handler.get_snapshot.return_value = {"last_price": 103.0}

    mock_event_bus = MagicMock()
    async def _publish(event):
        return None
    mock_event_bus.publish.side_effect = _publish

    mock_state_store = MagicMock()

    em = ExitManager(
        broker=mock_broker,
        position_tracker=mock_tracker,
        event_bus=mock_event_bus,
        state_store=mock_state_store,
        data_handler=mock_data_handler,
    )

    signals = await em.check_positions()

    # Ensure an exit signal was generated
    assert len(signals) == 1

    # Ensure only the public upsert API was called to persist the pending_exit update
    mock_tracker.upsert_position.assert_called_once()
    mock_tracker.persist_position.assert_not_called()
*** End Patch