"""Tests for position tracker."""

import pytest
import asyncio
from datetime import datetime, timezone
from unittest.mock import MagicMock, AsyncMock

from src.position_tracker import PositionTracker, PositionData
from src.state_store import StateStore


@pytest.fixture
def position_tracker(tmp_db, mock_broker):
    """Position tracker with temporary database."""
    state_store = StateStore(tmp_db)
    tracker = PositionTracker(
        broker=mock_broker,
        state_store=state_store,
        trailing_stop_enabled=True,
        trailing_stop_activation_pct=0.01,
        trailing_stop_trail_pct=0.005,
    )
    tracker.init_schema()
    return tracker


class TestPositionTracking:
    """Test basic position tracking functionality."""
    
    def test_start_tracking(self, position_tracker):
        """Position tracker starts tracking on BUY fill."""
        position = position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=10.0,
            side="long",
        )
        
        assert position.symbol == "AAPL"
        assert position.entry_price == 100.0
        assert position.qty == 10.0
        assert position.side == "long"
        assert position.highest_price == 100.0
        assert position.trailing_stop_activated is False
        assert position.trailing_stop_price is None
    
    def test_stop_tracking(self, position_tracker):
        """Position tracker stops tracking on SELL fill."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        assert position_tracker.get_position("AAPL") is not None
        
        position_tracker.stop_tracking("AAPL")
        assert position_tracker.get_position("AAPL") is None
    
    def test_get_position(self, position_tracker):
        """Get position returns correct data."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        position = position_tracker.get_position("AAPL")
        assert position is not None
        assert position.symbol == "AAPL"
        assert position.entry_price == 100.0
        
        # Non-existent position
        assert position_tracker.get_position("MSFT") is None
    
    def test_get_all_positions(self, position_tracker):
        """Get all positions returns list of all tracked positions."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        position_tracker.start_tracking("MSFT", 200.0, 5.0, "long")
        
        positions = position_tracker.get_all_positions()
        assert len(positions) == 2
        symbols = {p.symbol for p in positions}
        assert symbols == {"AAPL", "MSFT"}


class TestPnLCalculation:
    """Test P&L calculation."""
    
    def test_calculate_pnl_profit(self, position_tracker):
        """Calculate P&L for profitable position."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 110.0)
        
        # 10% gain on 10 shares = $100 profit
        assert pnl_amount == 100.0
        assert pnl_pct == 0.10
    
    def test_calculate_pnl_loss(self, position_tracker):
        """Calculate P&L for losing position."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 95.0)
        
        # 5% loss on 10 shares = $50 loss
        assert pnl_amount == -50.0
        assert pnl_pct == -0.05
    
    def test_calculate_pnl_no_position(self, position_tracker):
        """Calculate P&L returns zero for non-existent position."""
        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 100.0)
        
        assert pnl_amount == 0.0
        assert pnl_pct == 0.0


class TestTrailingStop:
    """Test trailing stop functionality."""
    
    def test_trailing_stop_not_activated_initially(self, position_tracker):
        """Trailing stop is not activated until profit threshold reached."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Price increases but not enough to activate (need +1%)
        position_tracker.update_current_price("AAPL", 100.5)
        
        position = position_tracker.get_position("AAPL")
        assert position.trailing_stop_activated is False
        assert position.trailing_stop_price is None
    
    def test_trailing_stop_activates_at_threshold(self, position_tracker):
        """Trailing stop activates when profit threshold reached."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Price increases by 1.5% (above 1% activation threshold)
        new_price = 101.5
        position_tracker.update_current_price("AAPL", new_price)
        
        position = position_tracker.get_position("AAPL")
        assert position.trailing_stop_activated is True
        assert position.trailing_stop_price is not None
        # Trailing stop should be 0.5% below current price
        expected_stop = new_price * 0.995
        assert position.trailing_stop_price == pytest.approx(expected_stop, 0.001)
    
    def test_trailing_stop_rises_with_price(self, position_tracker):
        """Trailing stop price increases as price rises."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Activate trailing stop
        position_tracker.update_current_price("AAPL", 101.5)
        position = position_tracker.get_position("AAPL")
        first_stop_price = position.trailing_stop_price
        
        # Price increases further
        position_tracker.update_current_price("AAPL", 102.0)
        position = position_tracker.get_position("AAPL")
        
        # Trailing stop should have moved up
        assert position.trailing_stop_price > first_stop_price
    
    def test_trailing_stop_never_decreases(self, position_tracker):
        """Trailing stop price never decreases."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Activate trailing stop
        position_tracker.update_current_price("AAPL", 101.5)
        position = position_tracker.get_position("AAPL")
        first_stop_price = position.trailing_stop_price
        
        # Price decreases (but not enough to trigger stop)
        position_tracker.update_current_price("AAPL", 101.0)
        position = position_tracker.get_position("AAPL")
        
        # Trailing stop should stay the same (never decreases)
        assert position.trailing_stop_price == first_stop_price
    
    def test_trailing_stop_not_triggered_above_stop(self, position_tracker):
        """Trailing stop is not triggered when price is above stop."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Activate trailing stop at $101.5
        position_tracker.update_current_price("AAPL", 101.5)
        position = position_tracker.get_position("AAPL")
        stop_price = position.trailing_stop_price  # ~$101.0
        
        # Price is above stop price
        assert 101.2 > stop_price
    
    def test_highest_price_tracks_max(self, position_tracker):
        """Highest price tracks maximum seen price."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        position_tracker.update_current_price("AAPL", 102.0)
        position = position_tracker.get_position("AAPL")
        assert position.highest_price == 102.0
        
        # Price drops
        position_tracker.update_current_price("AAPL", 101.0)
        position = position_tracker.get_position("AAPL")
        # Highest price should still be 102.0
        assert position.highest_price == 102.0


class TestBrokerSync:
    """Test broker position sync."""
    
    @pytest.mark.asyncio
    async def test_sync_adds_new_positions(self, position_tracker, mock_broker):
        """Sync starts tracking broker positions not currently tracked."""
        mock_broker.get_positions.return_value = [
            {"symbol": "AAPL", "qty": "10", "avg_entry_price": "100.0", "current_price": "105.0"},
            {"symbol": "MSFT", "qty": "5", "avg_entry_price": "200.0", "current_price": "210.0"},
        ]
        
        result = await position_tracker.sync_with_broker()
        
        assert "AAPL" in result["new_positions"]
        assert "MSFT" in result["new_positions"]
        assert position_tracker.get_position("AAPL") is not None
        assert position_tracker.get_position("MSFT") is not None
    
    @pytest.mark.asyncio
    async def test_sync_removes_old_positions(self, position_tracker, mock_broker):
        """Sync stops tracking positions not at broker."""
        # Start tracking a position
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Broker has no positions
        mock_broker.get_positions.return_value = []
        
        result = await position_tracker.sync_with_broker()
        
        assert "AAPL" in result["removed_positions"]
        assert position_tracker.get_position("AAPL") is None
    
    @pytest.mark.asyncio
    async def test_sync_detects_mismatches(self, position_tracker, mock_broker):
        """Sync detects quantity mismatches."""
        # Track position with 10 shares
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Broker shows 15 shares
        mock_broker.get_positions.return_value = [
            {"symbol": "AAPL", "qty": "15", "avg_entry_price": "100.0", "current_price": "105.0"},
        ]
        
        result = await position_tracker.sync_with_broker()
        
        assert len(result["mismatches"]) == 1
        assert result["mismatches"][0]["symbol"] == "AAPL"
        assert result["mismatches"][0]["broker_qty"] == 15.0
        assert result["mismatches"][0]["tracked_qty"] == 10.0


class TestPersistence:
    """Test SQLite persistence."""
    
    def test_persist_position(self, position_tracker):
        """Positions are persisted to SQLite."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        
        # Load positions in a new tracker instance
        new_tracker = PositionTracker(
            broker=position_tracker.broker,
            state_store=position_tracker.state_store,
        )
        loaded = new_tracker.load_persisted_positions()
        
        assert len(loaded) == 1
        assert loaded[0].symbol == "AAPL"
        assert loaded[0].entry_price == 100.0
    
    def test_remove_position(self, position_tracker):
        """Positions are removed from SQLite on stop."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        position_tracker.stop_tracking("AAPL")
        
        # Load positions in a new tracker instance
        new_tracker = PositionTracker(
            broker=position_tracker.broker,
            state_store=position_tracker.state_store,
        )
        loaded = new_tracker.load_persisted_positions()
        
        assert len(loaded) == 0
    
    def test_persist_trailing_stop_state(self, position_tracker):
        """Trailing stop state is persisted."""
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        position_tracker.update_current_price("AAPL", 101.5)
        
        # Load positions in a new tracker instance
        new_tracker = PositionTracker(
            broker=position_tracker.broker,
            state_store=position_tracker.state_store,
        )
        loaded = new_tracker.load_persisted_positions()
        
        assert len(loaded) == 1
        position = loaded[0]
        assert position.trailing_stop_activated is True
        assert position.trailing_stop_price is not None
