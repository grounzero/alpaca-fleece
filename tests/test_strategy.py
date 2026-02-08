"""Tests for strategy signal generation."""

from datetime import datetime, timedelta, timezone

import pandas as pd
import pytest

from src.event_bus import SignalEvent
from src.strategy.sma_crossover import SMACrossover


def create_price_series(prices: list, symbol: str = "AAPL") -> pd.DataFrame:
    """Create a price DataFrame for testing.

    Args:
        prices: List of closing prices
        symbol: Symbol name (for index naming)

    Returns:
        DataFrame with OHLCV columns
    """
    start_time = datetime(2024, 1, 1, 10, 0, tzinfo=timezone.utc)
    return pd.DataFrame(
        {
            "open": prices,
            "high": [p * 1.01 for p in prices],
            "low": [p * 0.99 for p in prices],
            "close": prices,
            "volume": [1000] * len(prices),
        },
        index=pd.DatetimeIndex([start_time + timedelta(minutes=i) for i in range(len(prices))]),
    )


# Test data pattern that reliably generates SMA crossovers
# Requires 51+ bars for SMA(20,50) warmup
# Pattern: baseline → decline → rally → decline → rally
RELIABLE_CROSSOVER_PRICES = (
    [150] * 25  # Baseline: fast SMA > slow SMA
    +
    # Gradual decline: fast crosses below slow (generates SELL)
    [140, 135, 130, 125, 120, 115, 110, 105, 100, 95, 90, 85, 80, 75, 70]
    +
    # Gradual rally: fast crosses above slow (generates BUY)
    [75, 80, 85, 90, 95, 100, 105, 110, 115, 120, 125, 130, 135, 140, 145]
    +
    # Another decline: fast crosses below slow (generates SELL)
    [140, 135, 130, 125, 120, 115, 110, 105, 100, 95]
    +
    # Another rally: fast crosses above slow (generates BUY)
    [100, 105, 110, 115, 120, 125, 130, 135, 140, 145]
)

# Inverse pattern for bearish test: low baseline → rally → decline
INVERSE_CROSSOVER_PRICES = (
    [100] * 25  # Low baseline: fast SMA < slow SMA
    +
    # Gradual rally: fast crosses above slow (generates BUY)
    [105, 110, 115, 120, 125, 130, 135, 140, 145, 150, 155, 160, 165, 170, 175]
    +
    # Gradual decline: fast crosses below slow (generates SELL)
    [170, 165, 160, 155, 150, 145, 140, 135, 130, 125, 120, 115, 110, 105, 100]
    +
    # Another rally: fast crosses above slow (generates BUY)
    [105, 110, 115, 120, 125, 130, 135, 140, 145, 150]
    +
    # Another decline: fast crosses below slow (generates SELL)
    [145, 140, 135, 130, 125, 120, 115, 110, 105, 100]
)


@pytest.mark.asyncio
async def test_strategy_bullish_crossover(state_store):
    """Test bullish crossover generates BUY signal with deterministic data."""
    strategy = SMACrossover(state_store)

    # Use reliable crossover pattern (75 bars, needs 51 for SMA(20,50))
    prices = RELIABLE_CROSSOVER_PRICES

    df = create_price_series(prices)

    # Process all bars and collect signals
    all_signals = []
    for i in range(strategy.get_required_history(), len(df)):
        signals = await strategy.on_bar("AAPL", df.iloc[: i + 1])
        all_signals.extend(signals)

    # Should get multiple signals including at least one BUY
    assert (
        len(all_signals) >= 2
    ), f"Expected at least 2 signals, got {len(all_signals)}: {[s.signal_type for s in all_signals]}"

    # Verify we have BUY signals
    buy_signals = [s for s in all_signals if s.signal_type == "BUY"]
    assert (
        len(buy_signals) >= 1
    ), f"Expected at least 1 BUY signal, got {[s.signal_type for s in all_signals]}"

    # Verify signal properties
    assert buy_signals[0].symbol == "AAPL"
    assert "confidence" in buy_signals[0].metadata


@pytest.mark.asyncio
async def test_strategy_bearish_crossover(state_store):
    """Test bearish crossover generates SELL signal with deterministic data."""
    strategy = SMACrossover(state_store)

    # Use inverse pattern that starts low and rallies first
    prices = INVERSE_CROSSOVER_PRICES

    df = create_price_series(prices)

    # Process all bars and collect signals
    all_signals = []
    for i in range(strategy.get_required_history(), len(df)):
        signals = await strategy.on_bar("AAPL", df.iloc[: i + 1])
        all_signals.extend(signals)

    # Should get multiple signals including at least one SELL
    assert (
        len(all_signals) >= 2
    ), f"Expected at least 2 signals, got {len(all_signals)}: {[s.signal_type for s in all_signals]}"

    # Verify we have SELL signals
    sell_signals = [s for s in all_signals if s.signal_type == "SELL"]
    assert (
        len(sell_signals) >= 1
    ), f"Expected at least 1 SELL signal, got {[s.signal_type for s in all_signals]}"

    # Verify signal properties
    assert sell_signals[0].symbol == "AAPL"
    assert "confidence" in sell_signals[0].metadata


@pytest.mark.asyncio
async def test_sma_crossover_prevents_duplicate_consecutive_signals(state_store):
    """Strategy should not emit duplicate consecutive signals."""
    strategy = SMACrossover(state_store)

    # Create uptrend data
    df = pd.DataFrame(
        {
            "close": list(range(100, 130)),
        },
        index=pd.DatetimeIndex(
            [datetime(2024, 1, 1, 10, i, tzinfo=timezone.utc) for i in range(30)]
        ),
    )

    # First signal
    signals1 = await strategy.on_bar("AAPL", df)

    # Same data again (no crossover)
    signals2 = await strategy.on_bar("AAPL", df)

    # Second call should not emit duplicate signals
    # (same SMA period and direction)
    if signals1 and signals2:
        for s1 in signals1:
            for s2 in signals2:
                if s1.metadata["sma_period"] == s2.metadata["sma_period"]:
                    # Same SMA period shouldn't repeat same signal
                    assert not (s1.signal_type == s2.signal_type)


@pytest.mark.asyncio
async def test_sma_crossover_no_signal_without_crossover(state_store):
    """Strategy should not emit signal if no crossover occurs."""
    strategy = SMACrossover(state_store)

    # Create flat data - no crossover possible (SMAs will be equal/parallel)
    prices = [100.0] * 100  # Flat line - no cross
    df = create_price_series(prices)

    signals = await strategy.on_bar("AAPL", df)

    # Flat data should never produce a crossover signal
    assert (
        len(signals) == 0
    ), f"Expected no signals for flat data, got {len(signals)}: {[s.signal_type for s in signals]}"


@pytest.mark.asyncio
async def test_strategy_alternate_signals(state_store):
    """Test strategy emits alternating BUY and SELL signals with clear trend changes."""
    strategy = SMACrossover(state_store)

    # Use reliable crossover pattern with multiple trend changes
    prices = RELIABLE_CROSSOVER_PRICES

    df = create_price_series(prices)

    # Process bars and collect signals
    all_signals = []
    for i in range(strategy.get_required_history(), len(df)):
        signals = await strategy.on_bar("AAPL", df.iloc[: i + 1])
        all_signals.extend(signals)

    # Should have multiple signals
    signal_types = [s.signal_type for s in all_signals]

    # Verify we have both BUY and SELL
    assert "BUY" in signal_types, f"Expected BUY signals, got {signal_types}"
    assert "SELL" in signal_types, f"Expected SELL signals, got {signal_types}"

    # Should have at least 4 signals (2 SELL, 2 BUY from alternating trends)
    assert (
        len(all_signals) >= 4
    ), f"Expected at least 4 signals, got {len(all_signals)}: {signal_types}"

    # Verify signals alternate for the same SMA period
    # (Strategy prevents duplicates via state tracking)
    for i in range(len(all_signals) - 1):
        if all_signals[i].metadata.get("sma_period") == all_signals[i + 1].metadata.get(
            "sma_period"
        ):
            assert (
                all_signals[i].signal_type != all_signals[i + 1].signal_type
            ), f"Same SMA period should not emit consecutive {all_signals[i].signal_type} signals"


@pytest.mark.asyncio
async def test_strategy_returns_list(state_store):
    """Test that strategy returns List[SignalEvent] not single SignalEvent."""
    strategy = SMACrossover(state_store)

    # Create simple data with enough bars
    df = create_price_series(list(range(100, 160)))

    # Get signals
    signals = await strategy.on_bar("AAPL", df)

    # Should be a list
    assert isinstance(signals, list)

    # If signals exist, they should be SignalEvent instances
    for signal in signals:
        assert isinstance(signal, SignalEvent)
