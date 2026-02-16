"""Tests for risk manager."""

from datetime import datetime, timezone
from unittest.mock import MagicMock

import pytest

from src.data_handler import DataHandler
from src.event_bus import SignalEvent
from src.risk_manager import RiskManager, RiskManagerError


@pytest.mark.asyncio
async def test_risk_manager_refuses_on_kill_switch(mock_broker, state_store, config):
    """Risk manager refuses to trade when kill-switch is active."""
    # Setup
    state_store.set_state("kill_switch", "true")
    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Should raise
    with pytest.raises(RiskManagerError, match="Kill-switch active"):
        await risk_mgr.check_signal(signal)


@pytest.mark.asyncio
async def test_risk_manager_refuses_on_circuit_breaker(mock_broker, state_store, config):
    """Risk manager refuses to trade when circuit breaker is tripped."""
    state_store.set_state("circuit_breaker_state", "tripped")
    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    with pytest.raises(RiskManagerError, match="Circuit breaker tripped"):
        await risk_mgr.check_signal(signal)


@pytest.mark.asyncio
async def test_risk_manager_refuses_when_market_closed(mock_broker, state_store, config):
    """Risk manager refuses when market is closed."""
    # Setup broker to return market closed
    mock_broker.get_clock.return_value = {
        "is_open": False,
        "next_open": None,
        "next_close": None,
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }

    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    with pytest.raises(RiskManagerError, match="Market not open"):
        await risk_mgr.check_signal(signal)


@pytest.mark.asyncio
async def test_risk_manager_refuses_when_daily_loss_exceeded(mock_broker, state_store, config):
    """Risk manager refuses when daily loss limit exceeded."""
    # Setup: equity $10k, max daily loss 5%, so max loss is $500
    # Set daily PnL to -$600 (exceeds limit)
    state_store.set_state("daily_pnl", "-600.0")

    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    with pytest.raises(RiskManagerError, match="Daily loss limit exceeded"):
        await risk_mgr.check_signal(signal)


@pytest.mark.asyncio
async def test_risk_manager_refuses_when_daily_trade_count_exceeded(
    mock_broker, state_store, config
):
    """Risk manager refuses when daily trade count exceeded."""
    # Config allows 20 trades/day
    state_store.set_state("daily_trade_count", "20")

    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    with pytest.raises(RiskManagerError, match="Daily trade count exceeded"):
        await risk_mgr.check_signal(signal)


@pytest.mark.asyncio
async def test_risk_manager_refuses_spread_too_wide(mock_broker, state_store, config):
    """Risk manager refuses when spread exceeds max_spread_pct."""
    # Config max_spread_pct: 0.5%, bid=$100, ask=$100.10 (0.1%)
    # Create spread too wide: bid=$100, ask=$100.60 (0.6%)
    data_handler = MagicMock(spec=DataHandler)
    data_handler.get_snapshot.return_value = {
        "bid": 100.0,
        "ask": 100.60,
        "bid_size": 100,
        "ask_size": 100,
    }

    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Should skip (return False), not error
    result = await risk_mgr.check_signal(signal)
    assert result is False


@pytest.mark.asyncio
async def test_risk_manager_refuses_if_spread_fetch_fails(mock_broker, state_store, config):
    """Risk manager REFUSES (does not bypass) if spread filter enabled but snapshot fetch fails."""
    # Spread filter enabled (max_spread_pct=0.005)
    # Snapshot fetch returns None
    data_handler = MagicMock(spec=DataHandler)
    data_handler.get_snapshot.return_value = None

    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Must refuse (raise), not bypass
    with pytest.raises(RiskManagerError, match="snapshot unavailable"):
        await risk_mgr.check_signal(signal)


@pytest.mark.asyncio
async def test_risk_manager_skips_on_low_bar_trades(mock_broker, state_store, config):
    """Risk manager skips signal if bar trade count too low."""
    # Config min_bar_trades: 10, set to 5
    data_handler = MagicMock(spec=DataHandler)
    data_handler.get_snapshot.return_value = {
        "bid": 100.0,
        "ask": 100.1,
    }

    import pandas as pd

    df = pd.DataFrame(
        {
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000],
            "trade_count": [5],  # Below min_bar_trades (10)
        }
    )
    data_handler.get_dataframe.return_value = df

    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Should skip (return False)
    result = await risk_mgr.check_signal(signal)
    assert result is False


@pytest.mark.asyncio
async def test_risk_manager_passes_all_checks(mock_broker, state_store, config):
    """Risk manager passes when all checks pass."""
    data_handler = MagicMock(spec=DataHandler)
    data_handler.get_snapshot.return_value = {
        "bid": 100.0,
        "ask": 100.1,
    }

    import pandas as pd

    df = pd.DataFrame(
        {
            "trade_count": [15],  # Above minimum
        }
    )
    data_handler.get_dataframe.return_value = df

    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    signal = SignalEvent(
        symbol="AAPL",
        signal_type="BUY",
        timestamp=datetime.now(timezone.utc),
        metadata={},
    )

    # Should pass
    result = await risk_mgr.check_signal(signal)
    assert result is True


# =============================================================================
# Exit Order Tests (check_exit_order)
# =============================================================================


@pytest.mark.asyncio
async def test_check_exit_order_passes_when_market_open(mock_broker, state_store, config):
    """Exit order validation passes when market is open."""
    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    result = await risk_mgr.check_exit_order("AAPL", "sell", 10.0)
    assert result is True


@pytest.mark.asyncio
async def test_check_exit_order_blocked_when_market_closed(mock_broker, state_store, config):
    """Exit orders blocked when market closed."""
    mock_broker.get_clock.return_value = {
        "is_open": False,
        "next_open": None,
        "next_close": None,
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }

    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    with pytest.raises(RiskManagerError, match="Market not open"):
        await risk_mgr.check_exit_order("AAPL", "sell", 10.0)


@pytest.mark.asyncio
async def test_check_exit_order_blocked_on_kill_switch(mock_broker, state_store, config):
    """Exit orders blocked when kill switch active."""
    state_store.set_state("kill_switch", "true")

    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    with pytest.raises(RiskManagerError, match="Kill-switch active"):
        await risk_mgr.check_exit_order("AAPL", "sell", 10.0)


@pytest.mark.asyncio
async def test_check_exit_order_skips_position_size_check(mock_broker, state_store, config):
    """Exit order validation skips position size checks."""
    # Set up a scenario that would fail position size for entry
    # But should pass for exit
    state_store.set_state("daily_trade_count", "20")  # Would fail entry

    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    # Exit order should still pass (no daily trade count check)
    result = await risk_mgr.check_exit_order("AAPL", "sell", 10.0)
    assert result is True


@pytest.mark.asyncio
async def test_check_exit_order_skips_spread_filter(mock_broker, state_store, config):
    """Exit order validation skips spread filter."""
    # Spread filter would fail for entry if snapshot returns None
    data_handler = MagicMock(spec=DataHandler)
    data_handler.get_snapshot.return_value = None  # Would fail entry

    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    # Exit order should still pass (no spread check)
    result = await risk_mgr.check_exit_order("AAPL", "sell", 10.0)
    assert result is True


@pytest.mark.asyncio
async def test_check_exit_order_validates_during_extended_hours(mock_broker, state_store, config):
    """Exit order validation works during extended hours for crypto."""
    # Add crypto symbol to config
    config["symbols"]["crypto_symbols"] = ["BTC/USD"]

    data_handler = MagicMock(spec=DataHandler)
    risk_mgr = RiskManager(mock_broker, data_handler, state_store, config)

    result = await risk_mgr.check_exit_order("BTC/USD", "sell", 0.5)
    assert result is True
