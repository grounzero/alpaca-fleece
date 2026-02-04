"""Tests for order manager."""
import pytest
from datetime import datetime
from unittest.mock import AsyncMock, MagicMock
import pytz

from src.order_manager import OrderManager
from src.config import Config
from src.state_store import StateStore
from src.event_bus import SignalEvent
from pathlib import Path
import tempfile


@pytest.fixture
def state_store():
    """Create temporary state store."""
    with tempfile.TemporaryDirectory() as tmpdir:
        db_path = Path(tmpdir) / "test.db"
        store = StateStore(db_path)
        yield store
        store.close()


@pytest.fixture
def config():
    """Create test configuration."""
    return Config(
        alpaca_api_key="test_key",
        alpaca_secret_key="test_secret",
        alpaca_paper=True,
        allow_live_trading=False,
        symbols=["AAPL"],
        bar_timeframe="1Min",
        stream_feed="iex",
        max_position_pct=0.10,
        max_daily_loss_pct=0.05,
        max_trades_per_day=20,
        sma_fast=10,
        sma_slow=30,
        dry_run=False,
        allow_extended_hours=False,
        log_level="INFO",
        kill_switch=False,
        circuit_breaker_reset=False,
    )


@pytest.fixture
def broker_mock():
    """Create mock broker."""
    broker = AsyncMock()
    broker.get_account = AsyncMock(return_value={
        "equity": 10000.0,
        "cash": 5000.0,
        "buying_power": 10000.0,
    })
    broker.submit_order = AsyncMock(return_value={
        "id": "alpaca_order_123",
        "client_order_id": "test_client_id",
        "symbol": "AAPL",
        "side": "buy",
        "qty": 10,
        "filled_qty": 10,
        "status": "filled",
    })
    return broker


@pytest.fixture
def risk_manager_mock():
    """Create mock risk manager."""
    risk_manager = MagicMock()
    risk_manager.validate_signal = AsyncMock(return_value=(True, "Validated"))
    risk_manager.calculate_qty = MagicMock(return_value=10)
    risk_manager.reset_failures = MagicMock()
    return risk_manager


@pytest.fixture
def order_manager(config, state_store, broker_mock, risk_manager_mock):
    """Create order manager instance."""
    logger = MagicMock()
    return OrderManager(config, state_store, broker_mock, risk_manager_mock, logger)


def test_generate_client_order_id_deterministic(order_manager):
    """Test client_order_id is deterministic."""
    signal_ts = datetime(2024, 1, 1, 10, 0, 0)

    id1 = order_manager.generate_client_order_id("SMA", "AAPL", "1Min", signal_ts, "buy")
    id2 = order_manager.generate_client_order_id("SMA", "AAPL", "1Min", signal_ts, "buy")

    assert id1 == id2
    assert len(id1) == 16


def test_generate_client_order_id_different_inputs(order_manager):
    """Test different inputs produce different IDs."""
    signal_ts = datetime(2024, 1, 1, 10, 0, 0)

    id1 = order_manager.generate_client_order_id("SMA", "AAPL", "1Min", signal_ts, "buy")
    id2 = order_manager.generate_client_order_id("SMA", "AAPL", "1Min", signal_ts, "sell")
    id3 = order_manager.generate_client_order_id("SMA", "MSFT", "1Min", signal_ts, "buy")

    assert id1 != id2
    assert id1 != id3
    assert id2 != id3


@pytest.mark.asyncio
async def test_process_signal_success(order_manager, state_store):
    """Test successful signal processing."""
    signal = SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="SMA_Crossover",
        signal_timestamp=datetime(2024, 1, 1, 10, 0, 0, tzinfo=pytz.UTC),
        metadata={"close": 150.0},
    )

    order_intent = await order_manager.process_signal(signal)

    assert order_intent is not None
    assert order_intent.symbol == "AAPL"
    assert order_intent.side == "buy"
    assert order_intent.qty == 10

    # Check state store
    assert state_store.order_exists(order_intent.client_order_id)


@pytest.mark.asyncio
async def test_process_signal_duplicate_prevention(order_manager, state_store):
    """Test duplicate order prevention."""
    signal = SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="SMA_Crossover",
        signal_timestamp=datetime(2024, 1, 1, 10, 0, 0, tzinfo=pytz.UTC),
        metadata={"close": 150.0},
    )

    # Submit first order
    order_intent1 = await order_manager.process_signal(signal)
    assert order_intent1 is not None

    # Submit same signal again (same client_order_id)
    order_intent2 = await order_manager.process_signal(signal)

    # Second submission should be prevented
    assert order_intent2 is None


@pytest.mark.asyncio
async def test_process_signal_dry_run(config, state_store, broker_mock, risk_manager_mock):
    """Test dry run mode."""
    config_dry = Config(
        **{**config.__dict__, "dry_run": True}
    )

    logger = MagicMock()
    order_manager = OrderManager(config_dry, state_store, broker_mock, risk_manager_mock, logger)

    signal = SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="SMA_Crossover",
        signal_timestamp=datetime(2024, 1, 1, 10, 0, 0, tzinfo=pytz.UTC),
        metadata={"close": 150.0},
    )

    order_intent = await order_manager.process_signal(signal)

    assert order_intent is not None

    # Broker submit_order should NOT be called in dry run
    broker_mock.submit_order.assert_not_called()

    # Check order status is "dry_run"
    order = state_store.get_order_intent(order_intent.client_order_id)
    assert order["status"] == "dry_run"


@pytest.mark.asyncio
async def test_process_signal_invalid(order_manager, risk_manager_mock):
    """Test signal rejection by risk manager."""
    risk_manager_mock.validate_signal = AsyncMock(return_value=(False, "Kill switch activated"))

    signal = SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="SMA_Crossover",
        signal_timestamp=datetime(2024, 1, 1, 10, 0, 0, tzinfo=pytz.UTC),
        metadata={"close": 150.0},
    )

    order_intent = await order_manager.process_signal(signal)

    assert order_intent is None


@pytest.mark.asyncio
async def test_process_signal_increments_trade_count(order_manager, state_store):
    """Test daily trade count is incremented."""
    initial_count = state_store.get_daily_trade_count()

    signal = SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="SMA_Crossover",
        signal_timestamp=datetime(2024, 1, 1, 10, 0, 0, tzinfo=pytz.UTC),
        metadata={"close": 150.0},
    )

    await order_manager.process_signal(signal)

    final_count = state_store.get_daily_trade_count()
    assert final_count == initial_count + 1


@pytest.mark.asyncio
async def test_process_signal_kill_switch_file_blocks_order(config, state_store, broker_mock, risk_manager_mock):
    """Test kill switch FILE blocks order immediately before submission.

    This is a CRITICAL safety test. Even after passing risk validation,
    the kill switch file should be checked IMMEDIATELY before order submission.
    This catches cases where kill switch is created AFTER bot startup.
    """
    from unittest.mock import patch

    logger = MagicMock()
    order_manager = OrderManager(config, state_store, broker_mock, risk_manager_mock, logger)

    signal = SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="SMA_Crossover",
        signal_timestamp=datetime(2024, 1, 1, 10, 0, 0, tzinfo=pytz.UTC),
        metadata={"close": 150.0},
    )

    # Mock the kill switch file to exist (simulating file created after startup)
    with patch("src.order_manager.Path") as mock_path:
        mock_file = MagicMock()
        mock_file.exists.return_value = True
        mock_path.return_value.__truediv__.return_value = mock_file

        order_intent = await order_manager.process_signal(signal)

        # Order should be blocked
        assert order_intent is None

        # Broker should NOT have been called
        broker_mock.submit_order.assert_not_called()

        # Order should be saved with blocked status
        # Find the order by checking state store (order was saved before kill switch check)
        # The status should be updated to blocked_kill_switch


@pytest.mark.asyncio
async def test_process_signal_succeeds_without_kill_switch_file(order_manager, state_store, broker_mock):
    """Test that orders succeed normally when kill switch file doesn't exist."""
    signal = SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="SMA_Crossover",
        signal_timestamp=datetime(2024, 1, 1, 11, 0, 0, tzinfo=pytz.UTC),  # Different timestamp
        metadata={"close": 150.0},
    )

    order_intent = await order_manager.process_signal(signal)

    # Order should succeed
    assert order_intent is not None
    assert order_intent.symbol == "AAPL"

    # Broker should have been called
    broker_mock.submit_order.assert_called_once()
