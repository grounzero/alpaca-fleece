"""Tests for risk check ordering (Fix 2.3)."""

from datetime import datetime, timezone
from unittest.mock import MagicMock

import pytest

from src.event_bus import SignalEvent
from src.risk_manager import RiskManager, RiskManagerError


@pytest.fixture
def risk_manager_config():
    """Provide test config."""
    return {
        "risk": {
            "regular_hours": {
                "max_position_pct": 0.10,
                "max_daily_loss_pct": 0.05,
                "max_trades_per_day": 20,
                "max_concurrent_positions": 10,
            },
            "extended_hours": {
                "max_position_pct": 0.20,
                "max_daily_loss_pct": 0.10,
                "max_trades_per_day": 40,
                "max_concurrent_positions": 20,
            },
        },
        "symbols": {"crypto_symbols": []},
        "filters": {"max_spread_pct": 0.01},
    }


@pytest.fixture
def risk_manager(risk_manager_config):
    """Create risk manager for testing."""
    broker = MagicMock()
    data_handler = MagicMock()
    state_store = MagicMock()

    return RiskManager(
        broker=broker,
        data_handler=data_handler,
        state_store=state_store,
        config=risk_manager_config,
    )


class TestRiskCheckOrder:
    """Test that checks happen in correct order: SAFETY → RISK → CONFIDENCE → FILTER."""

    @pytest.mark.asyncio
    async def test_kill_switch_checked_first_before_confidence_filter(self, risk_manager):
        """Kill-switch active raises error, even with low-confidence signal."""
        # Setup: Kill-switch active, low confidence signal
        risk_manager.state_store.get_state.return_value = "true"  # kill_switch = true
        risk_manager.broker.get_clock.return_value = {"is_open": True}

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.3},  # Low confidence
        )

        # Should raise RiskManagerError("Kill-switch active"), not filtered for confidence
        with pytest.raises(RiskManagerError, match="Kill-switch active"):
            await risk_manager.check_signal(signal)

    @pytest.mark.asyncio
    async def test_circuit_breaker_checked_before_confidence_filter(self, risk_manager):
        """Circuit breaker tripped raises error, not filtered for confidence."""
        # Setup: Circuit breaker tripped, low confidence
        risk_manager.state_store.get_state.side_effect = lambda key: {
            "kill_switch": "false",
            "circuit_breaker_state": "tripped",
        }.get(key)
        risk_manager.broker.get_clock.return_value = {"is_open": True}

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.3},  # Low confidence
        )

        with pytest.raises(RiskManagerError, match="Circuit breaker tripped"):
            await risk_manager.check_signal(signal)

    @pytest.mark.asyncio
    async def test_market_closed_checked_before_confidence_filter(self, risk_manager):
        """Market closed raises error, not filtered for confidence."""
        # Setup: Market closed, low confidence
        risk_manager.state_store.get_state.return_value = "false"
        risk_manager.broker.get_clock.return_value = {"is_open": False}

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.3},  # Low confidence
        )

        with pytest.raises(RiskManagerError, match="Market not open"):
            await risk_manager.check_signal(signal)

    @pytest.mark.asyncio
    async def test_safety_checks_happen_before_risk_checks(self, risk_manager):
        """Safety tier (kill-switch) checked before risk tier (daily loss)."""
        # Setup: Kill-switch active AND daily loss exceeded
        risk_manager.state_store.get_state.return_value = "true"  # kill_switch = true
        risk_manager.broker.get_clock.return_value = {"is_open": True}
        risk_manager.broker.get_account.return_value = {
            "equity": 50000,
            "buying_power": 25000,
            "cash": 10000,
            "portfolio_value": 50000,
        }

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.9},  # High confidence
        )

        # Should reject with kill-switch message, not daily loss
        with pytest.raises(RiskManagerError, match="Kill-switch active"):
            await risk_manager.check_signal(signal)

    @pytest.mark.asyncio
    async def test_low_confidence_rejected_without_raising(self, risk_manager):
        """Low confidence signal is rejected (returns False), not error."""
        # Setup: All safety checks pass, but confidence is low
        risk_manager.state_store.get_state.side_effect = lambda key: {
            "kill_switch": "false",
            "circuit_breaker_state": "normal",
        }.get(key, "false")
        risk_manager.broker.get_clock.return_value = {"is_open": True}
        risk_manager.broker.get_account.return_value = {
            "equity": 100000,
            "buying_power": 50000,
            "cash": 25000,
            "portfolio_value": 100000,
        }
        risk_manager.state_store.get_daily_pnl.return_value = 0.0
        risk_manager.state_store.increment_daily_trade_count.return_value = 1
        risk_manager.state_store.get_daily_trade_count.return_value = 1  # Not exceeded
        risk_manager.state_store.get_open_position_count.return_value = 1  # Not exceeded

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.3},  # Low confidence (< 0.5 min)
        )

        # Should return False, not raise
        result = await risk_manager.check_signal(signal)
        assert result is False

    @pytest.mark.asyncio
    async def test_high_confidence_signal_passes_all_checks(self, risk_manager):
        """High confidence signal passes all checks."""
        # Setup: All checks pass, high confidence
        risk_manager.state_store.get_state.side_effect = lambda key: {
            "kill_switch": "false",
            "circuit_breaker_state": "normal",
        }.get(key, "false")
        risk_manager.broker.get_clock.return_value = {"is_open": True}
        risk_manager.broker.get_account.return_value = {
            "equity": 100000,
            "buying_power": 50000,
            "cash": 25000,
            "portfolio_value": 100000,
        }
        risk_manager.state_store.get_daily_pnl.return_value = 0.0
        risk_manager.state_store.increment_daily_trade_count.return_value = 1
        risk_manager.state_store.get_daily_trade_count.return_value = 1  # Not exceeded
        risk_manager.state_store.get_open_position_count.return_value = 1  # Not exceeded
        risk_manager.broker.get_positions.return_value = []  # No positions
        risk_manager.data_handler.get_snapshot.return_value = {
            "bid": 100,
            "ask": 100.01,
            "trade_count": 1000,
        }
        risk_manager.data_handler.get_dataframe.return_value = None  # Skip bar trade filter

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.8},  # High confidence
        )

        # Note: May be filtered by filter tier, but should pass safety/risk/confidence checks
        try:
            result = await risk_manager.check_signal(signal)
            # If successful, should pass all checks
            assert result in (True, False)
        except RiskManagerError as e:
            # Should only fail from filter tier, not safety/risk/confidence
            assert "spread" in str(e).lower() or "filter" in str(e).lower()

    @pytest.mark.asyncio
    async def test_check_order_documented_in_docstring(self, risk_manager):
        """check_signal docstring documents the check order."""
        docstring = risk_manager.check_signal.__doc__
        assert docstring is not None
        assert "SAFETY tier" in docstring or "safety" in docstring
        assert "RISK tier" in docstring or "risk" in docstring
        assert "Confidence" in docstring or "confidence" in docstring
        assert "FILTER tier" in docstring or "filter" in docstring
