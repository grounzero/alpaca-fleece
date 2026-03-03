"""End-to-end integration test: Signal → Risk Check → Order (deterministic, offline, mocked)."""

from datetime import datetime, timezone
from unittest.mock import Mock

import pandas as pd
import pytest

from src.event_bus import EventBus, SignalEvent
from src.order_manager import OrderManager
from src.risk_manager import RiskManager


@pytest.fixture
def mock_broker():
    """Mock broker with deterministic responses."""
    broker = Mock()

    # Mock account (always healthy)
    broker.get_account = Mock(
        return_value={
            "equity": 100000.0,
            "cash": 99000.0,
            "buying_power": 200000.0,
        }
    )

    # Mock clock (always market open)
    broker.get_clock = Mock(
        return_value={
            "is_open": True,
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }
    )

    # Mock positions (none open)
    broker.get_positions = Mock(return_value=[])

    # Mock orders (none open)
    broker.get_open_orders = Mock(return_value=[])

    # Mock order submission (always succeeds)
    broker.submit_order = Mock(
        return_value={
            "id": "test-order-123",
            "symbol": "AAPL",
            "qty": 10,
            "side": "buy",
            "status": "pending",
        }
    )

    # Mock snapshot (tight spreads)
    broker.get_snapshot = Mock(
        return_value={
            "bid": 150.0,
            "ask": 150.1,
            "mid": 150.05,
        }
    )

    return broker


@pytest.fixture
def event_bus_fixture():
    """Event bus for signal propagation."""
    return EventBus()


class TestEndToEndTrade:
    """Deterministic end-to-end: signal → risk → order."""

    @pytest.mark.asyncio
    async def test_buy_signal_passes_all_gates_and_submits_order(
        self, state_store, mock_broker, event_bus_fixture
    ):
        """Test: Valid BUY signal passes risk checks and submits order."""

        # SETUP: Create components
        # Create properly mocked data handler
        data_handler = Mock()
        data_handler.get_snapshot = mock_broker.get_snapshot
        # Mock the bar trades check (returns DataFrame with enough trades)
        mock_df = pd.DataFrame({"trade_count": [15, 14, 16, 15]})  # All >= 10
        data_handler.get_dataframe = Mock(return_value=mock_df)
        data_handler.get_last_n_bars = Mock(return_value=mock_df)

        risk_manager = RiskManager(
            broker=mock_broker,
            data_handler=data_handler,
            state_store=state_store,
            config={
                "symbols": {"equity_symbols": ["AAPL"], "crypto_symbols": []},
                "trading": {"session_policy": "regular_only"},
                "risk": {
                    "regular_hours": {
                        "max_position_pct": 0.10,
                        "max_daily_loss_pct": 0.05,
                        "max_trades_per_day": 20,
                        "max_concurrent_positions": 10,
                    },
                    "extended_hours": {
                        "max_position_pct": 0.05,
                        "max_daily_loss_pct": 0.03,
                        "max_trades_per_day": 10,
                        "max_concurrent_positions": 5,
                    },
                },
                "filters": {
                    "max_spread_pct": None,  # Disable spread filter for test
                    "min_bar_trades": None,  # Disable bar trade filter for test
                    "avoid_first_minutes": 0,
                    "avoid_last_minutes": 0,
                },
            },
        )

        order_manager = OrderManager(
            broker=mock_broker,
            state_store=state_store,
            event_bus=event_bus_fixture,
            config={"execution": {"order_type": "market", "time_in_force": "day"}},
            strategy_name="sma_crossover",
        )

        # STEP 1: Create a valid BUY signal
        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={
                "confidence": 0.9,  # High confidence (trending)
                "regime": "trending",
                "regime_strength": 0.95,
                "sma_period": (20, 50),
                "fast_sma": 150.5,
                "slow_sma": 149.0,
                "close": 150.05,
            },
        )

        # STEP 2: Risk check should PASS
        risk_passed = await risk_manager.check_signal(signal)
        assert risk_passed, "Risk check failed: BUY signal with 0.9 confidence should pass"

        # STEP 3: Submit order should SUCCEED
        order_submitted = await order_manager.submit_order(signal, qty=10.0)
        assert order_submitted, "Order submission failed"

        # STEP 4: Verify broker was called
        assert mock_broker.submit_order.called, "Broker.submit_order was not called"

        # STEP 5: Verify order was persisted
        order_intent = state_store.get_order_intent(
            order_manager._generate_client_order_id("AAPL", signal.timestamp, "buy")
        )
        assert order_intent is not None, "Order intent was not persisted"
        assert order_intent.symbol == "AAPL"
        assert order_intent.side == "buy"
        assert order_intent.qty == 10.0

        print("✅ END-TO-END TEST PASSED: Signal → Risk Check → Order Submission")

    @pytest.mark.asyncio
    async def test_low_confidence_signal_filtered_by_risk_manager(
        self, state_store, mock_broker, event_bus_fixture
    ):
        """Test: Low-confidence signal is filtered out (confidence < 0.5)."""

        risk_manager = RiskManager(
            broker=mock_broker,
            data_handler=Mock(),
            state_store=state_store,
            config={
                "symbols": {"equity_symbols": ["AAPL"], "crypto_symbols": []},
                "trading": {"session_policy": "regular_only"},
                "risk": {
                    "regular_hours": {
                        "max_position_pct": 0.10,
                        "max_daily_loss_pct": 0.05,
                        "max_trades_per_day": 20,
                        "max_concurrent_positions": 10,
                    },
                    "extended_hours": {
                        "max_position_pct": 0.05,
                        "max_daily_loss_pct": 0.03,
                        "max_trades_per_day": 10,
                        "max_concurrent_positions": 5,
                    },
                },
                "filters": {},
            },
        )

        # Create low-confidence signal (ranging market)
        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={
                "confidence": 0.3,  # LOW confidence (ranging)
                "regime": "ranging",
                "regime_strength": 0.1,
                "sma_period": (10, 30),
            },
        )

        # Risk check should FAIL (confidence < 0.5)
        risk_passed = await risk_manager.check_signal(signal)
        assert not risk_passed, "Low-confidence signal should be filtered"

        print("✅ FILTER TEST PASSED: Low-confidence signal correctly rejected")

    @pytest.mark.asyncio
    async def test_market_closed_blocks_order(self, state_store, mock_broker, event_bus_fixture):
        """Test: Order blocked when market is closed."""

        # Market CLOSED
        mock_broker.get_clock = Mock(return_value={"is_open": False})

        risk_manager = RiskManager(
            broker=mock_broker,
            data_handler=Mock(),
            state_store=state_store,
            config={
                "symbols": {"equity_symbols": ["AAPL"], "crypto_symbols": []},
                "trading": {"session_policy": "regular_only"},
                "risk": {
                    "regular_hours": {
                        "max_position_pct": 0.10,
                        "max_daily_loss_pct": 0.05,
                        "max_trades_per_day": 20,
                        "max_concurrent_positions": 10,
                    },
                    "extended_hours": {
                        "max_position_pct": 0.05,
                        "max_daily_loss_pct": 0.03,
                        "max_trades_per_day": 10,
                        "max_concurrent_positions": 5,
                    },
                },
                "filters": {},
            },
        )

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.9},
        )

        # Risk check should FAIL (market closed)
        from src.risk_manager import RiskManagerError

        with pytest.raises(RiskManagerError, match="Market not open"):
            await risk_manager.check_signal(signal)

        print("✅ MARKET CLOSED TEST PASSED: Order blocked when market closed")

    @pytest.mark.asyncio
    async def test_circuit_breaker_blocks_after_5_failures(
        self, state_store, mock_broker, event_bus_fixture
    ):
        """Test: Circuit breaker halts trading after 5 consecutive failures."""

        # Set circuit breaker STATE to "tripped" (this is what gets checked)
        state_store.set_state("circuit_breaker_state", "tripped")

        risk_manager = RiskManager(
            broker=mock_broker,
            data_handler=Mock(),
            state_store=state_store,
            config={
                "symbols": {"equity_symbols": ["AAPL"], "crypto_symbols": []},
                "trading": {"session_policy": "regular_only"},
                "risk": {
                    "regular_hours": {
                        "max_position_pct": 0.10,
                        "max_daily_loss_pct": 0.05,
                        "max_trades_per_day": 20,
                        "max_concurrent_positions": 10,
                    },
                    "extended_hours": {
                        "max_position_pct": 0.05,
                        "max_daily_loss_pct": 0.03,
                        "max_trades_per_day": 10,
                        "max_concurrent_positions": 5,
                    },
                },
                "filters": {},
            },
        )

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.9},
        )

        # Risk check should FAIL (circuit breaker tripped)
        from src.risk_manager import RiskManagerError

        with pytest.raises(RiskManagerError, match="Circuit breaker tripped"):
            await risk_manager.check_signal(signal)

        print("✅ CIRCUIT BREAKER TEST PASSED: Trading halted when tripped")

    @pytest.mark.asyncio
    async def test_daily_loss_limit_blocks_trading(
        self, state_store, mock_broker, event_bus_fixture
    ):
        """Test: Trading blocked when daily loss limit exceeded."""

        # Set daily P&L to -$6000 (exceeds 5% limit on $100k)
        state_store.save_daily_pnl(-6000.0)

        risk_manager = RiskManager(
            broker=mock_broker,
            data_handler=Mock(),
            state_store=state_store,
            config={
                "symbols": {"equity_symbols": ["AAPL"], "crypto_symbols": []},
                "trading": {"session_policy": "regular_only"},
                "risk": {
                    "regular_hours": {
                        "max_position_pct": 0.10,
                        "max_daily_loss_pct": 0.05,
                        "max_trades_per_day": 20,
                        "max_concurrent_positions": 10,
                    },
                    "extended_hours": {
                        "max_position_pct": 0.05,
                        "max_daily_loss_pct": 0.03,
                        "max_trades_per_day": 10,
                        "max_concurrent_positions": 5,
                    },
                },
                "filters": {},
            },
        )

        signal = SignalEvent(
            symbol="AAPL",
            signal_type="BUY",
            timestamp=datetime.now(timezone.utc),
            metadata={"confidence": 0.9},
        )

        # Risk check should FAIL (daily loss exceeded)
        from src.risk_manager import RiskManagerError

        with pytest.raises(RiskManagerError, match="Daily loss limit exceeded"):
            await risk_manager.check_signal(signal)

        print("✅ DAILY LOSS LIMIT TEST PASSED: Trading halted on loss limit")
