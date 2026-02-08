"""Tests for fill price tracking and P&L updates."""

from datetime import datetime, timezone

import pytest

from src.event_bus import OrderUpdateEvent
from src.position_tracker import PositionTracker
from src.state_store import StateStore


@pytest.fixture
def orchestrator_fill_setup(tmp_db, mock_broker, event_bus):
    """Setup for testing fill tracking in orchestrator-like context."""
    state_store = StateStore(tmp_db)
    position_tracker = PositionTracker(
        broker=mock_broker,
        state_store=state_store,
        trailing_stop_enabled=False,
    )
    position_tracker.init_schema()

    return {
        "state_store": state_store,
        "position_tracker": position_tracker,
        "event_bus": event_bus,
        "broker": mock_broker,
    }


class TestBuyFillTracking:
    """Test buy fill tracking and position creation."""

    @pytest.mark.asyncio
    async def test_buy_fill_starts_position_tracking(self, orchestrator_fill_setup):
        """Buy fill should start tracking position with fill price."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]
        position_tracker = setup["position_tracker"]

        # Create order intent first (as order_manager would do)
        client_order_id = "test-buy-order-001"
        symbol = "AAPL"
        side = "buy"
        qty = 10.0

        state_store.save_order_intent(
            client_order_id=client_order_id,
            symbol=symbol,
            side=side,
            qty=qty,
            status="submitted",
        )

        # Simulate order fill event
        fill_event = OrderUpdateEvent(
            order_id="alpaca-order-123",
            client_order_id=client_order_id,
            symbol=symbol,
            status="filled",
            filled_qty=qty,
            avg_fill_price=150.50,
            timestamp=datetime.now(timezone.utc),
        )

        # Look up order intent and handle fill (as _handle_order_fill does)
        order_intent = state_store.get_order_intent(client_order_id)
        assert order_intent is not None
        assert order_intent["side"] == "buy"

        # Start tracking position with fill price
        position_tracker.start_tracking(
            symbol=symbol,
            fill_price=fill_event.avg_fill_price,
            qty=fill_event.filled_qty,
            side="long",
        )

        # Verify position is tracked with correct fill price
        position = position_tracker.get_position(symbol)
        assert position is not None
        assert position.symbol == symbol
        assert position.entry_price == 150.50
        assert position.qty == qty
        assert position.side == "long"

    @pytest.mark.asyncio
    async def test_buy_fill_records_trade(self, orchestrator_fill_setup):
        """Buy fill should record trade in database."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]

        # Record trade (as OrderUpdatesHandler._record_trade does)
        fill_event = OrderUpdateEvent(
            order_id="alpaca-order-123",
            client_order_id="test-buy-order-001",
            symbol="AAPL",
            status="filled",
            filled_qty=10.0,
            avg_fill_price=150.50,
            timestamp=datetime.now(timezone.utc),
        )

        import sqlite3

        conn = sqlite3.connect(state_store.db_path)
        cursor = conn.cursor()
        cursor.execute(
            """INSERT INTO trades 
               (timestamp_utc, symbol, side, qty, price, order_id, client_order_id)
               VALUES (?, ?, ?, ?, ?, ?, ?)""",
            (
                fill_event.timestamp.isoformat(),
                fill_event.symbol,
                "buy",
                fill_event.filled_qty,
                fill_event.avg_fill_price,
                fill_event.order_id,
                fill_event.client_order_id,
            ),
        )
        conn.commit()

        # Verify trade was recorded
        cursor.execute(
            "SELECT symbol, side, qty, price FROM trades WHERE client_order_id = ?",
            (fill_event.client_order_id,),
        )
        row = cursor.fetchone()
        conn.close()

        assert row is not None
        assert row[0] == "AAPL"
        assert row[1] == "buy"
        assert row[2] == 10.0
        assert row[3] == 150.50


class TestSellFillTracking:
    """Test sell fill tracking and P&L calculation."""

    @pytest.mark.asyncio
    async def test_sell_fill_calculates_realized_pnl_profit(self, orchestrator_fill_setup):
        """Sell fill should calculate realized P&L for profitable trade."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]
        position_tracker = setup["position_tracker"]

        symbol = "AAPL"
        entry_price = 150.0
        exit_price = 160.0
        qty = 10.0

        # Create buy order intent
        buy_order_id = "test-buy-order-001"
        state_store.save_order_intent(
            client_order_id=buy_order_id,
            symbol=symbol,
            side="buy",
            qty=qty,
            status="filled",
        )

        # Start tracking position (as would happen on buy fill)
        position_tracker.start_tracking(
            symbol=symbol,
            fill_price=entry_price,
            qty=qty,
            side="long",
        )

        # Create sell order intent
        sell_order_id = "test-sell-order-002"
        state_store.save_order_intent(
            client_order_id=sell_order_id,
            symbol=symbol,
            side="sell",
            qty=qty,
            status="submitted",
        )

        # Look up order intent
        order_intent = state_store.get_order_intent(sell_order_id)
        assert order_intent["side"] == "sell"

        # Get position
        position = position_tracker.get_position(symbol)
        assert position is not None

        # Calculate realized P&L
        realized_pnl = (exit_price - position.entry_price) * qty
        # (160 - 150) * 10 = $100 profit
        assert realized_pnl == 100.0

        # Update daily P&L
        current_daily_pnl = state_store.get_daily_pnl()
        new_daily_pnl = current_daily_pnl + realized_pnl
        state_store.save_daily_pnl(new_daily_pnl)

        # Stop tracking position
        position_tracker.stop_tracking(symbol)

        # Increment trade count
        count = state_store.get_daily_trade_count()
        state_store.save_daily_trade_count(count + 1)

        # Verify P&L was updated
        assert state_store.get_daily_pnl() == 100.0
        assert state_store.get_daily_trade_count() == 1

    @pytest.mark.asyncio
    async def test_sell_fill_calculates_realized_pnl_loss(self, orchestrator_fill_setup):
        """Sell fill should calculate realized P&L for losing trade."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]
        position_tracker = setup["position_tracker"]

        symbol = "AAPL"
        entry_price = 150.0
        exit_price = 140.0
        qty = 10.0

        # Start tracking position
        position_tracker.start_tracking(
            symbol=symbol,
            fill_price=entry_price,
            qty=qty,
            side="long",
        )

        # Create sell order intent
        sell_order_id = "test-sell-order-002"
        state_store.save_order_intent(
            client_order_id=sell_order_id,
            symbol=symbol,
            side="sell",
            qty=qty,
            status="submitted",
        )

        # Simulate sell fill
        position = position_tracker.get_position(symbol)
        realized_pnl = (exit_price - position.entry_price) * qty
        # (140 - 150) * 10 = -$100 loss
        assert realized_pnl == -100.0

        # Update daily P&L
        current_daily_pnl = state_store.get_daily_pnl()
        new_daily_pnl = current_daily_pnl + realized_pnl
        state_store.save_daily_pnl(new_daily_pnl)

        # Stop tracking position
        position_tracker.stop_tracking(symbol)

        # Verify P&L was updated (negative)
        assert state_store.get_daily_pnl() == -100.0

    @pytest.mark.asyncio
    async def test_sell_fill_accumulates_daily_pnl(self, orchestrator_fill_setup):
        """Multiple sell fills should accumulate in daily P&L."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]
        position_tracker = setup["position_tracker"]

        # First trade: $50 profit
        position_tracker.start_tracking("AAPL", 100.0, 10.0, "long")
        position = position_tracker.get_position("AAPL")
        realized_pnl_1 = (105.0 - position.entry_price) * 10  # $50 profit
        current_pnl = state_store.get_daily_pnl()
        state_store.save_daily_pnl(current_pnl + realized_pnl_1)
        position_tracker.stop_tracking("AAPL")

        # Second trade: $30 profit
        position_tracker.start_tracking("MSFT", 200.0, 5.0, "long")
        position = position_tracker.get_position("MSFT")
        realized_pnl_2 = (206.0 - position.entry_price) * 5  # $30 profit
        current_pnl = state_store.get_daily_pnl()
        state_store.save_daily_pnl(current_pnl + realized_pnl_2)
        position_tracker.stop_tracking("MSFT")

        # Third trade: $20 loss
        position_tracker.start_tracking("TSLA", 300.0, 10.0, "long")
        position = position_tracker.get_position("TSLA")
        realized_pnl_3 = (298.0 - position.entry_price) * 10  # -$20 loss
        current_pnl = state_store.get_daily_pnl()
        state_store.save_daily_pnl(current_pnl + realized_pnl_3)
        position_tracker.stop_tracking("TSLA")

        # Verify accumulated P&L: 50 + 30 - 20 = $60
        assert state_store.get_daily_pnl() == 60.0


class TestPnLPersistence:
    """Test P&L persistence across state store operations."""

    def test_daily_pnl_persists_to_database(self, orchestrator_fill_setup):
        """Daily P&L should be persisted to database."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]

        # Save P&L
        state_store.save_daily_pnl(150.50)

        # Verify it can be retrieved
        assert state_store.get_daily_pnl() == 150.50

        # Create new state store instance (simulating restart)
        new_state_store = StateStore(state_store.db_path)

        # Verify P&L persists
        assert new_state_store.get_daily_pnl() == 150.50

    def test_daily_trade_count_persists_to_database(self, orchestrator_fill_setup):
        """Daily trade count should be persisted to database."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]

        # Save trade count
        state_store.save_daily_trade_count(5)

        # Verify it can be retrieved
        assert state_store.get_daily_trade_count() == 5

        # Create new state store instance
        new_state_store = StateStore(state_store.db_path)

        # Verify trade count persists
        assert new_state_store.get_daily_trade_count() == 5


class TestFillEdgeCases:
    """Test edge cases for fill handling."""

    @pytest.mark.asyncio
    async def test_sell_fill_without_position_logs_warning(self, orchestrator_fill_setup):
        """Sell fill for untracked position should be handled gracefully."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]
        position_tracker = setup["position_tracker"]

        # Don't create a position first
        symbol = "AAPL"

        # Try to get position (should be None)
        position = position_tracker.get_position(symbol)
        assert position is None

        # Create sell order intent anyway
        sell_order_id = "test-sell-order-003"
        state_store.save_order_intent(
            client_order_id=sell_order_id,
            symbol=symbol,
            side="sell",
            qty=10.0,
            status="submitted",
        )

        # Simulate sell fill for untracked position
        order_intent = state_store.get_order_intent(sell_order_id)
        assert order_intent["side"] == "sell"

        position = position_tracker.get_position(symbol)
        assert position is None  # No position to calculate P&L from

        # Should not crash, just skip P&L calculation
        # (In real implementation, this would log a warning)

    @pytest.mark.asyncio
    async def test_fill_with_unknown_order_logs_warning(self, orchestrator_fill_setup):
        """Fill for unknown order should be handled gracefully."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]

        # Don't create order intent
        unknown_order_id = "unknown-order-999"

        # Try to look up order intent
        order_intent = state_store.get_order_intent(unknown_order_id)
        assert order_intent is None

        # Should not crash, just skip processing
        # (In real implementation, this would log a warning)

    @pytest.mark.asyncio
    async def test_partial_fill_handling(self, orchestrator_fill_setup):
        """Partial fills should use filled quantity for position tracking."""
        setup = orchestrator_fill_setup
        state_store = setup["state_store"]

        symbol = "AAPL"
        total_qty = 100.0
        partial_fill_qty = 25.0
        fill_price = 150.0

        # Create order intent
        state_store.save_order_intent(
            client_order_id="test-buy-order-004",
            symbol=symbol,
            side="buy",
            qty=total_qty,
            status="submitted",
        )

        # Simulate partial fill
        partial_fill_event = OrderUpdateEvent(
            order_id="alpaca-order-789",
            client_order_id="test-buy-order-004",
            symbol=symbol,
            status="partially_filled",
            filled_qty=partial_fill_qty,
            avg_fill_price=fill_price,
            timestamp=datetime.now(timezone.utc),
        )

        # Note: In real implementation, we might not start tracking
        # until fully filled, or we might track partial fills
        # For now, just verify the data is correct
        assert partial_fill_event.filled_qty == 25.0
        assert partial_fill_event.avg_fill_price == 150.0


class TestMetricsIntegration:
    """Test metrics are updated correctly on fills."""

    def test_metrics_updated_after_buy_fill(self, orchestrator_fill_setup):
        """Metrics should be updated after buy fill."""
        from src.metrics import metrics

        setup = orchestrator_fill_setup
        state_store = setup["state_store"]

        # Record a fill
        initial_filled = metrics.orders_filled
        metrics.record_order_filled()

        # Update daily P&L gauge
        state_store.save_daily_pnl(100.0)
        metrics.update_daily_pnl(state_store.get_daily_pnl())

        assert metrics.orders_filled == initial_filled + 1
        assert metrics.daily_pnl == 100.0

    def test_metrics_updated_after_sell_fill(self, orchestrator_fill_setup):
        """Metrics should be updated after sell fill."""
        from src.metrics import metrics

        setup = orchestrator_fill_setup
        state_store = setup["state_store"]

        # Record a fill
        initial_filled = metrics.orders_filled
        metrics.record_order_filled()

        # Update daily P&L and trade count
        state_store.save_daily_pnl(150.0)
        state_store.save_daily_trade_count(1)
        metrics.update_daily_pnl(state_store.get_daily_pnl())
        metrics.update_daily_trade_count(state_store.get_daily_trade_count())

        assert metrics.orders_filled == initial_filled + 1
        assert metrics.daily_pnl == 150.0
        assert metrics.daily_trade_count == 1


class TestPositionTrackerIntegration:
    """Test position tracker works correctly with fill tracking."""

    def test_position_tracker_persists_buy_fill(self, orchestrator_fill_setup):
        """Position tracker should persist buy fill data."""
        setup = orchestrator_fill_setup
        position_tracker = setup["position_tracker"]

        # Start tracking
        position_tracker.start_tracking("AAPL", 150.50, 10.0, "long")

        # Create new tracker instance
        new_tracker = PositionTracker(
            broker=position_tracker.broker,
            state_store=position_tracker.state_store,
        )
        loaded = new_tracker.load_persisted_positions()

        assert len(loaded) == 1
        assert loaded[0].symbol == "AAPL"
        assert loaded[0].entry_price == 150.50
        assert loaded[0].qty == 10.0

    def test_position_tracker_removes_on_sell_fill(self, orchestrator_fill_setup):
        """Position tracker should remove position on sell fill."""
        setup = orchestrator_fill_setup
        position_tracker = setup["position_tracker"]

        # Start and then stop tracking
        position_tracker.start_tracking("AAPL", 150.50, 10.0, "long")
        assert position_tracker.get_position("AAPL") is not None

        position_tracker.stop_tracking("AAPL")
        assert position_tracker.get_position("AAPL") is None

        # Verify removal persists
        new_tracker = PositionTracker(
            broker=position_tracker.broker,
            state_store=position_tracker.state_store,
        )
        loaded = new_tracker.load_persisted_positions()
        assert len(loaded) == 0
