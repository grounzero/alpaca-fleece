"""Ensure conservative behavior when fetching positions fails.

When `broker.get_positions` raises, `OrderManager` should treat position
as unknown and prefer exiting exposure for a SELL signal (i.e., map to
`EXIT_LONG`) rather than attempting to open a short (`ENTER_SHORT`).
"""

from datetime import datetime, timezone

import pytest

from src.event_bus import SignalEvent
from src.order_manager import OrderManager


@pytest.mark.asyncio
async def test_sell_with_positions_fetch_failure_does_not_invoke_gate(
    state_store, event_bus, mock_broker, config
):
    # Simulate broker.get_positions raising an exception
    mock_broker.get_positions.side_effect = Exception("boom")

    # Replace gate_try_accept with a spy so we can ensure it's not called
    from unittest.mock import MagicMock

    state_store.gate_try_accept = MagicMock(return_value=True)

    # Ensure we don't actually submit orders
    config["DRY_RUN"] = True

    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    sig = SignalEvent(
        symbol="FOO", signal_type="SELL", timestamp=datetime.now(timezone.utc), metadata={}
    )
    res = await order_mgr.submit_order(sig, qty=1.0)

    # In DRY_RUN the method returns True for successful submission paths.
    assert res is True

    # Because get_positions failed, SELL should be treated as EXIT_LONG and
    # therefore gate_try_accept (entry gate) must NOT be called.
    state_store.gate_try_accept.assert_not_called()


@pytest.mark.asyncio
async def test_buy_while_currently_short_treated_as_exit_and_does_not_invoke_gate(
    state_store, event_bus, mock_broker, config
):
    # Simulate broker reporting a short position for the symbol
    mock_broker.get_positions.return_value = [{"symbol": "FOO", "qty": -2}]

    # Spy on gate_try_accept to ensure entry gate is NOT called for a cover
    from unittest.mock import MagicMock

    state_store.gate_try_accept = MagicMock(return_value=True)

    # Ensure we don't actually submit orders
    config["DRY_RUN"] = True

    order_mgr = OrderManager(
        broker=mock_broker,
        state_store=state_store,
        event_bus=event_bus,
        config=config,
        strategy_name="sma_crossover",
    )

    sig = SignalEvent(
        symbol="FOO", signal_type="BUY", timestamp=datetime.now(timezone.utc), metadata={}
    )
    res = await order_mgr.submit_order(sig, qty=1.0)

    # In DRY_RUN the method returns True for successful submission paths.
    assert res is True

    # BUY while currently short should be treated as an exit (cover) and
    # therefore gate_try_accept (entry gate) must NOT be called.
    state_store.gate_try_accept.assert_not_called()
