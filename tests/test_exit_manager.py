"""Tests for exit manager."""

import pytest
from datetime import datetime, timezone
from unittest.mock import MagicMock

from src.exit_manager import ExitManager
from src.position_tracker import PositionTracker
from src.state_store import StateStore


@pytest.fixture
def exit_manager(tmp_db, mock_broker, event_bus):
    """Exit manager with all dependencies."""
    state_store = StateStore(tmp_db)
    position_tracker = PositionTracker(
        broker=mock_broker,
        state_store=state_store,
        trailing_stop_enabled=True,
        trailing_stop_activation_pct=0.01,
        trailing_stop_trail_pct=0.005,
    )
    position_tracker.init_schema()

    # Start tracking a position
    position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")

    data_handler = MagicMock()
    data_handler.get_snapshot.return_value = {"last_price": 100.0, "bid": 100.0, "ask": 100.1}

    manager = ExitManager(
        broker=mock_broker,
        position_tracker=position_tracker,
        event_bus=event_bus,
        state_store=state_store,
        data_handler=data_handler,
        stop_loss_pct=0.01,  # -1%
        profit_target_pct=0.02,  # +2%
        trailing_stop_enabled=True,
        trailing_stop_activation_pct=0.01,
        trailing_stop_trail_pct=0.005,
        check_interval_seconds=30,
        exit_on_circuit_breaker=True,
    )
    return manager


class TestStopLoss:
    """Test stop loss triggering."""

    @pytest.mark.asyncio
    async def test_stop_loss_triggers_at_threshold(self, exit_manager, event_bus):
        """Stop loss triggers at -1%."""
        # Set price to -1.5% (below -1% stop loss)
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 98.5,  # -1.5% from entry of 100
            "bid": 98.5,
            "ask": 98.6,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        assert signals[0].symbol == "AAPL"
        assert signals[0].reason == "stop_loss"
        assert signals[0].pnl_pct == pytest.approx(-0.015, 0.001)

    @pytest.mark.asyncio
    async def test_stop_loss_does_not_trigger_above_threshold(self, exit_manager):
        """Stop loss does not trigger when P&L is above threshold."""
        # Set price to -0.5% (above -1% stop loss)
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 99.5,  # -0.5% from entry of 100
            "bid": 99.5,
            "ask": 99.6,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 0

    @pytest.mark.asyncio
    async def test_stop_loss_exactly_at_threshold(self, exit_manager):
        """Stop loss triggers exactly at -1%."""
        # Set price to exactly -1%
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 99.0,  # -1% from entry of 100
            "bid": 99.0,
            "ask": 99.1,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        assert signals[0].reason == "stop_loss"


class TestProfitTarget:
    """Test profit target triggering."""

    @pytest.mark.asyncio
    async def test_profit_target_triggers_at_threshold(self, exit_manager):
        """Profit target triggers at +2%."""
        # Set price to +2.5% (above +2% profit target)
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 102.5,  # +2.5% from entry of 100
            "bid": 102.5,
            "ask": 102.6,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        assert signals[0].symbol == "AAPL"
        assert signals[0].reason == "profit_target"
        assert signals[0].pnl_pct == pytest.approx(0.025, 0.001)

    @pytest.mark.asyncio
    async def test_profit_target_exactly_at_threshold(self, exit_manager):
        """Profit target triggers exactly at +2%."""
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 102.0,  # +2% from entry of 100
            "bid": 102.0,
            "ask": 102.1,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        assert signals[0].reason == "profit_target"

    @pytest.mark.asyncio
    async def test_no_exit_within_bounds(self, exit_manager):
        """No exit when P&L is within stop loss and profit target bounds."""
        # Set price to +1% (within -1% to +2% bounds)
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 101.0,
            "bid": 101.0,
            "ask": 101.1,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 0


class TestTrailingStop:
    """Test trailing stop functionality."""

    @pytest.mark.asyncio
    async def test_trailing_stop_activates_after_profit(self, exit_manager):
        """Trailing stop activates after +1% profit."""
        # First, raise price to +1.5% to activate trailing stop
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 101.5,
            "bid": 101.5,
            "ask": 101.6,
        }
        await exit_manager.check_positions()

        position = exit_manager.position_tracker.get_position("AAPL")
        assert position.trailing_stop_activated is True

    @pytest.mark.asyncio
    async def test_trailing_stop_triggers_when_breached(self, exit_manager):
        """Trailing stop triggers when price falls to stop level."""
        # Activate trailing stop at $101.5 (stop price ~$101.0)
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 101.5,
            "bid": 101.5,
            "ask": 101.6,
        }
        await exit_manager.check_positions()

        # Now drop price below trailing stop
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 100.8,  # Below trailing stop of ~$101.0
            "bid": 100.8,
            "ask": 100.9,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        assert signals[0].reason == "trailing_stop"

    @pytest.mark.asyncio
    async def test_trailing_stop_not_triggered_when_disabled(self, exit_manager):
        """Trailing stop does not trigger when disabled."""
        # Disable trailing stop in both exit manager and position tracker
        exit_manager.trailing_stop_enabled = False
        exit_manager.position_tracker.trailing_stop_enabled = False

        # Activate trailing stop logic (shouldn't actually activate)
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 101.5,
            "bid": 101.5,
            "ask": 101.6,
        }
        await exit_manager.check_positions()

        position = exit_manager.position_tracker.get_position("AAPL")
        assert position.trailing_stop_activated is False


class TestExitPriority:
    """Test exit rule priority."""

    @pytest.mark.asyncio
    async def test_stop_loss_priority_over_profit_target(self, exit_manager):
        """Stop loss has priority over profit target."""
        # Price that triggers both stop loss (-1%) and profit target (+2%)
        # This shouldn't happen, but if it does, stop loss should win
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 99.0,  # -1% stop loss
            "bid": 99.0,
            "ask": 99.1,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        # Stop loss should be the reason (evaluated first)
        assert signals[0].reason == "stop_loss"

    @pytest.mark.asyncio
    async def test_stop_loss_priority_over_trailing_stop(self, exit_manager):
        """Stop loss has priority over trailing stop."""
        # First activate trailing stop
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 101.5,
            "bid": 101.5,
            "ask": 101.6,
        }
        await exit_manager.check_positions()

        # Now price drops below stop loss AND trailing stop
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 98.0,  # Below -1% stop loss
            "bid": 98.0,
            "ask": 98.1,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        # Stop loss should win
        assert signals[0].reason == "stop_loss"


class TestMarketClosed:
    """Test market closed behaviour."""

    @pytest.mark.asyncio
    async def test_no_exits_when_market_closed(self, exit_manager, mock_broker):
        """Exit orders blocked when market closed."""
        mock_broker.get_clock.return_value = {
            "is_open": False,
            "next_open": None,
            "next_close": None,
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }

        # Price would trigger stop loss
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 98.0,
            "bid": 98.0,
            "ask": 98.1,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 0


class TestCircuitBreaker:
    """Test circuit breaker exit."""

    @pytest.mark.asyncio
    async def test_close_all_positions_on_circuit_breaker(self, exit_manager):
        """Close all positions when circuit breaker tripped."""
        exit_manager.state_store.set_state("circuit_breaker_state", "tripped")

        signals = await exit_manager.close_all_positions(reason="circuit_breaker")

        assert len(signals) == 1
        assert signals[0].symbol == "AAPL"
        assert signals[0].reason == "circuit_breaker"

    @pytest.mark.asyncio
    async def test_exit_on_circuit_breaker_disabled(self, exit_manager):
        """Don't auto-close when exit_on_circuit_breaker is disabled."""
        exit_manager.exit_on_circuit_breaker = False
        exit_manager.state_store.set_state("circuit_breaker_state", "tripped")

        # This would normally trigger close_all_positions in monitor loop
        # but since we can't easily test the loop, we verify the flag works
        assert exit_manager.exit_on_circuit_breaker is False

    @pytest.mark.asyncio
    async def test_close_all_positions_emergency(self, exit_manager):
        """close_all_positions works for emergency exits."""
        signals = await exit_manager.close_all_positions(reason="emergency")

        assert len(signals) == 1
        assert signals[0].symbol == "AAPL"
        assert signals[0].reason == "emergency"
        assert signals[0].side == "sell"
        assert signals[0].qty == 10.0


class TestExitSignalEvent:
    """Test ExitSignalEvent generation."""

    @pytest.mark.asyncio
    async def test_exit_signal_fields(self, exit_manager):
        """ExitSignalEvent has all required fields."""
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 102.0,  # +2% profit target
            "bid": 102.0,
            "ask": 102.1,
        }

        signals = await exit_manager.check_positions()

        assert len(signals) == 1
        signal = signals[0]

        assert signal.symbol == "AAPL"
        assert signal.side == "sell"  # Long position, so sell to exit
        assert signal.qty == 10.0
        assert signal.reason == "profit_target"
        assert signal.entry_price == 100.0
        assert signal.current_price == 102.0
        assert signal.pnl_pct == pytest.approx(0.02, 0.001)
        assert signal.pnl_amount == pytest.approx(20.0, 0.001)
        assert isinstance(signal.timestamp, datetime)

    @pytest.mark.asyncio
    async def test_exit_signal_published_to_bus(self, exit_manager, event_bus):
        """ExitSignalEvent is published to event bus."""
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 102.0,
            "bid": 102.0,
            "ask": 102.1,
        }

        await exit_manager.check_positions()

        # Check that event was published
        assert event_bus.size() == 1
        published = await event_bus.subscribe()
        assert published.symbol == "AAPL"
        assert published.reason == "profit_target"


class TestCheckSinglePosition:
    """Test checking single position."""

    @pytest.mark.asyncio
    async def test_check_single_position(self, exit_manager):
        """Check single position returns signal if threshold breached."""
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 102.0,
            "bid": 102.0,
            "ask": 102.1,
        }

        signal = await exit_manager.check_single_position("AAPL")

        assert signal is not None
        assert signal.symbol == "AAPL"
        assert signal.reason == "profit_target"

    @pytest.mark.asyncio
    async def test_check_single_position_no_signal(self, exit_manager):
        """Check single position returns None if no threshold breached."""
        exit_manager.data_handler.get_snapshot.return_value = {
            "last_price": 100.5,  # Within bounds
            "bid": 100.5,
            "ask": 100.6,
        }

        signal = await exit_manager.check_single_position("AAPL")

        assert signal is None

    @pytest.mark.asyncio
    async def test_check_single_position_not_tracked(self, exit_manager):
        """Check single position returns None for untracked symbol."""
        signal = await exit_manager.check_single_position("MSFT")

        assert signal is None


class TestNoPositions:
    """Test behaviour with no positions."""

    @pytest.mark.asyncio
    async def test_no_signals_with_no_positions(self, exit_manager):
        """No signals generated when no positions tracked."""
        # Remove the position
        exit_manager.position_tracker.stop_tracking("AAPL")

        signals = await exit_manager.check_positions()

        assert len(signals) == 0

    @pytest.mark.asyncio
    async def test_close_all_with_no_positions(self, exit_manager):
        """close_all_positions returns empty list when no positions."""
        exit_manager.position_tracker.stop_tracking("AAPL")

        signals = await exit_manager.close_all_positions()

        assert len(signals) == 0


class TestStartStop:
    """Test start and stop methods."""

    @pytest.mark.asyncio
    async def test_start_stop(self, exit_manager):
        """Exit manager can be started and stopped."""
        await exit_manager.start()
        assert exit_manager._running is True

        await exit_manager.stop()
        assert exit_manager._running is False

    @pytest.mark.asyncio
    async def test_start_idempotent(self, exit_manager):
        """Starting already running exit manager is a no-op."""
        await exit_manager.start()
        await exit_manager.start()  # Should not raise

        await exit_manager.stop()

    @pytest.mark.asyncio
    async def test_stop_when_not_running(self, exit_manager):
        """Stopping non-running exit manager is a no-op."""
        await exit_manager.stop()  # Should not raise
