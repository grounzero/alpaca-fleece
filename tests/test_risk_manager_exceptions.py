from unittest.mock import MagicMock

import pytest

from src.async_broker_adapter import BrokerFatalError, BrokerTimeoutError
from src.risk_manager import RiskManager, RiskManagerError


@pytest.mark.asyncio
async def test_check_exit_order_transient_wraps():
    broker = MagicMock()
    broker.get_clock.side_effect = BrokerTimeoutError("timeout")

    rm = RiskManager(broker=broker, data_handler=MagicMock(), state_store=MagicMock(), config={})

    with pytest.raises(RiskManagerError, match="Clock fetch failed"):
        await rm.check_exit_order("AAPL", "sell", 1.0)


@pytest.mark.asyncio
async def test_check_exit_order_fatal_propagates():
    broker = MagicMock()
    broker.get_clock.side_effect = BrokerFatalError("fatal")

    rm = RiskManager(broker=broker, data_handler=MagicMock(), state_store=MagicMock(), config={})

    with pytest.raises(BrokerFatalError):
        await rm.check_exit_order("AAPL", "sell", 1.0)


@pytest.mark.asyncio
async def test_check_exit_order_programming_error_propagates():
    broker = MagicMock()
    # Return a malformed clock dict so accessing ['is_open'] raises KeyError
    broker.get_clock.return_value = {}

    rm = RiskManager(broker=broker, data_handler=MagicMock(), state_store=MagicMock(), config={})

    with pytest.raises(KeyError):
        await rm.check_exit_order("AAPL", "sell", 1.0)
