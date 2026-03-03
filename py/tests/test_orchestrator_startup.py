from unittest.mock import MagicMock

import pytest

from src.order_manager import OrderManager
from src.position_tracker import PositionTracker
from src.schema_manager import SchemaManager
from src.state_store import StateStore


@pytest.mark.asyncio
async def test_order_manager_receives_loaded_position_tracker(tmp_path):
    # Prepare a temporary DB for StateStore
    db_path = str(tmp_path / "state.db")
    SchemaManager.ensure_schema(db_path)
    state_store = StateStore(db_path)

    # Mock broker to avoid network calls; ensure sync_with_broker will call get_positions
    mock_broker = MagicMock()
    mock_broker.get_positions.return_value = []

    # Create position tracker and ensure load + reconcile are called
    pt = PositionTracker(
        broker=mock_broker,
        state_store=state_store,
        trailing_stop_enabled=False,
    )

    # Load persisted positions (none exist) and run reconcile/sync
    pt.load_persisted_positions()
    await pt.sync_with_broker()

    # Construct OrderManager with the prepared tracker
    cfg = {}
    om = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=MagicMock(),
        config=cfg,
        strategy_name="sma_crossover",
        position_tracker=pt,
    )

    # The OrderManager must have a non-null tracker and the broker's get_positions
    # was called during the tracker's sync (reconcile) step.
    assert om.position_tracker is pt
    assert mock_broker.get_positions.called
