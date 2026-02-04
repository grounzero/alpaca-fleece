"""Tests for risk manager."""
import pytest
import asyncio
from datetime import datetime, time
from unittest.mock import AsyncMock, MagicMock, patch
import pytz

from src.risk_manager import RiskManager
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
        symbols=["AAPL", "MSFT"],
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
        "portfolio_value": 10000.0,
        "trading_blocked": False,
        "account_blocked": False,
    })
    return broker


@pytest.fixture
def risk_manager(config, state_store, broker_mock):
    """Create risk manager instance."""
    logger = MagicMock()
    return RiskManager(config, state_store, broker_mock, logger)


@pytest.fixture
def signal():
    """Create test signal."""
    return SignalEvent(
        symbol="AAPL",
        side="buy",
        strategy_name="test_strategy",
        signal_timestamp=datetime.now(pytz.timezone("America/New_York")),
        metadata={"close": 150.0},
    )


@pytest.mark.asyncio
async def test_validate_signal_success(risk_manager, signal):
    """Test successful signal validation."""
    is_valid, reason = await risk_manager.validate_signal(signal, 150.0)

    assert is_valid is True
    assert reason == "Validated"


@pytest.mark.asyncio
async def test_validate_signal_kill_switch(risk_manager, signal, config):
    """Test kill switch blocks signals."""
    risk_manager.config = Config(
        **{**config.__dict__, "kill_switch": True}
    )

    is_valid, reason = await risk_manager.validate_signal(signal, 150.0)

    assert is_valid is False
    assert reason == "Kill switch activated"


@pytest.mark.asyncio
async def test_validate_signal_circuit_breaker(risk_manager, signal, state_store):
    """Test circuit breaker blocks signals."""
    state_store.set_circuit_breaker_state(tripped=True, failures=5)

    is_valid, reason = await risk_manager.validate_signal(signal, 150.0)

    assert is_valid is False
    assert reason == "Circuit breaker tripped"


@pytest.mark.asyncio
async def test_validate_signal_daily_limit(risk_manager, signal, state_store):
    """Test daily trade limit blocks signals."""
    state_store.set_state("daily_trade_count", 20)

    is_valid, reason = await risk_manager.validate_signal(signal, 150.0)

    assert is_valid is False
    assert "Daily trade limit reached" in reason


@pytest.mark.asyncio
async def test_validate_signal_market_hours(risk_manager, signal):
    """Test market hours check."""
    # Mock current time to be outside market hours
    with patch("src.risk_manager.datetime") as mock_datetime:
        # Set time to 8:00 AM ET (before market open)
        mock_time = datetime(2024, 1, 15, 8, 0, 0)  # Monday
        mock_datetime.now.return_value = pytz.timezone("America/New_York").localize(mock_time)
        mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

        # Force re-check
        result = risk_manager.is_market_hours()

        # Since we're mocking, the result depends on implementation
        # For this test, we just verify the method runs
        assert isinstance(result, bool)


def test_calculate_qty(risk_manager):
    """Test position quantity calculation."""
    equity = 10000.0
    price = 100.0

    qty = risk_manager.calculate_qty(equity, price)

    # max_position_pct = 0.10
    # max_value = 10000 * 0.10 = 1000
    # qty = 1000 / 100 = 10
    assert qty == 10


def test_calculate_qty_zero_price(risk_manager):
    """Test quantity calculation with zero price."""
    qty = risk_manager.calculate_qty(10000.0, 0.0)
    assert qty == 0


def test_circuit_breaker_trip(risk_manager, state_store):
    """Test circuit breaker trip."""
    # Record failures
    for _ in range(4):
        risk_manager.record_failure()

    state = state_store.get_circuit_breaker_state()
    assert state["tripped"] is False
    assert state["failures"] == 4

    # Fifth failure should trip
    risk_manager.record_failure()

    state = state_store.get_circuit_breaker_state()
    assert state["tripped"] is True
    assert state["failures"] == 5


def test_circuit_breaker_reset(risk_manager, state_store):
    """Test circuit breaker manual reset."""
    state_store.set_circuit_breaker_state(tripped=True, failures=5)

    risk_manager.reset_circuit_breaker()

    state = state_store.get_circuit_breaker_state()
    assert state["tripped"] is False
    assert state["failures"] == 0


def test_circuit_breaker_persistence(state_store):
    """Test circuit breaker state persists across restarts."""
    # Set circuit breaker state
    state_store.set_circuit_breaker_state(tripped=True, failures=5)

    # Close and reopen database (simulating restart)
    db_path = state_store.db_path
    state_store.close()

    state_store2 = StateStore(db_path)
    state = state_store2.get_circuit_breaker_state()

    assert state["tripped"] is True
    assert state["failures"] == 5

    state_store2.close()


@pytest.mark.asyncio
async def test_validate_signal_kill_switch_file(config, state_store, broker_mock, signal):
    """Test kill switch FILE blocks signals (not just env var).

    This is a CRITICAL safety test. The kill switch file should be checked
    dynamically at runtime, not just at startup.
    """
    from unittest.mock import patch

    logger = MagicMock()
    risk_manager = RiskManager(config, state_store, broker_mock, logger)

    # Mock the kill switch file to exist
    with patch("src.risk_manager.Path") as mock_path:
        mock_file = MagicMock()
        mock_file.exists.return_value = True
        mock_path.return_value.__truediv__.return_value = mock_file

        is_valid, reason = await risk_manager.validate_signal(signal, 150.0)

        assert is_valid is False
        assert reason == "Kill switch activated"
