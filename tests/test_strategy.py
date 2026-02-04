"""Tests for SMA crossover strategy."""
import pytest
import pandas as pd
from datetime import datetime, timedelta
from src.strategy.sma_crossover import SMACrossoverStrategy
from src.state_store import StateStore
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
def strategy(state_store):
    """Create strategy instance."""
    return SMACrossoverStrategy(fast_period=5, slow_period=10, state_store=state_store)


def create_price_series(prices, start_date=None):
    """Create DataFrame with price series."""
    if start_date is None:
        start_date = datetime(2024, 1, 1, 9, 30)

    data = []
    for i, price in enumerate(prices):
        timestamp = start_date + timedelta(minutes=i)
        data.append({
            "timestamp": timestamp,
            "open": price,
            "high": price,
            "low": price,
            "close": price,
            "volume": 1000,
        })

    df = pd.DataFrame(data)
    df.set_index("timestamp", inplace=True)
    return df


def test_strategy_requires_history(strategy):
    """Test strategy requires sufficient history."""
    assert strategy.get_required_history() == 12  # slow_period + 2


def test_strategy_no_signal_insufficient_data(strategy):
    """Test no signal with insufficient data."""
    prices = [100, 101, 102]
    df = create_price_series(prices)

    signal = strategy.on_bar("AAPL", df)

    assert signal is None


def test_strategy_bullish_crossover(strategy):
    """Test bullish crossover generates BUY signal."""
    # Create prices where fast SMA crosses above slow SMA
    # Start with declining prices, then rally
    prices = [100] * 10 + [95, 90, 85, 85, 90, 95, 100, 105, 110, 115, 120]

    df = create_price_series(prices)

    # Process bars until crossover
    signal = None
    for i in range(strategy.get_required_history(), len(df)):
        signal = strategy.on_bar("AAPL", df.iloc[:i+1])
        if signal:
            break

    assert signal is not None
    assert signal.side == "buy"
    assert signal.symbol == "AAPL"
    assert signal.strategy_name == "SMA_Crossover"


def test_strategy_bearish_crossover(strategy):
    """Test bearish crossover generates SELL signal."""
    # Create prices where fast SMA crosses below slow SMA
    # Start with rally, then decline
    prices = [100] * 10 + [105, 110, 115, 115, 110, 105, 100, 95, 90, 85, 80]

    df = create_price_series(prices)

    # Process bars until crossover
    signal = None
    for i in range(strategy.get_required_history(), len(df)):
        signal = strategy.on_bar("AAPL", df.iloc[:i+1])
        if signal:
            break

    assert signal is not None
    assert signal.side == "sell"
    assert signal.symbol == "AAPL"
    assert signal.strategy_name == "SMA_Crossover"


def test_strategy_no_signal_when_fast_above_slow(strategy):
    """Test no signal when fast > slow but no crossover."""
    # Fast already above slow, no crossover
    prices = [100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112]

    df = create_price_series(prices)

    signal = strategy.on_bar("AAPL", df)

    # Should be None because no crossover occurred
    # (fast was already above slow from the start)
    assert signal is None


def test_strategy_no_duplicate_signals(strategy, state_store):
    """Test strategy does not emit duplicate consecutive signals."""
    # Create bullish crossover
    prices = [100] * 10 + [95, 90, 85, 85, 90, 95, 100, 105, 110, 115, 120, 121, 122]

    df = create_price_series(prices)

    signals = []
    for i in range(strategy.get_required_history(), len(df)):
        signal = strategy.on_bar("AAPL", df.iloc[:i+1])
        if signal:
            signals.append(signal)

    # Should only get one BUY signal despite fast staying above slow
    assert len(signals) == 1
    assert signals[0].side == "buy"

    # Verify last signal is stored
    last_signal = state_store.get_last_signal("AAPL")
    assert last_signal == "BUY"


def test_strategy_alternate_signals(strategy, state_store):
    """Test strategy can alternate between BUY and SELL."""
    # Create prices with multiple crossovers
    prices = (
        [100] * 10 +  # Baseline
        [95, 90, 85, 90, 95, 100, 105, 110] +  # Rally (bullish crossover)
        [105, 100, 95, 90, 85, 80, 75, 70] +  # Decline (bearish crossover)
        [75, 80, 85, 90, 95, 100, 105, 110]  # Rally again (bullish crossover)
    )

    df = create_price_series(prices)

    signals = []
    for i in range(strategy.get_required_history(), len(df)):
        signal = strategy.on_bar("AAPL", df.iloc[:i+1])
        if signal:
            signals.append(signal.side)

    # Should get alternating signals
    assert len(signals) >= 2
    # Signals should alternate
    for i in range(len(signals) - 1):
        assert signals[i] != signals[i + 1]
