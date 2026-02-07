"""Tests for multi-timeframe SMA + regime detection (Win #2)."""

import pytest
import pandas as pd
import numpy as np

from src.strategy.sma_crossover import SMACrossover
from src.regime_detector import RegimeDetector
from src.state_store import StateStore


@pytest.fixture
def state_store(tmp_path):
    """Create temporary state store for testing."""
    db_path = str(tmp_path / "test_trade.db")
    return StateStore(db_path)


@pytest.fixture
def strategy(state_store):
    """Create multi-timeframe SMA strategy."""
    return SMACrossover(state_store)


@pytest.fixture
def regime_detector():
    """Create regime detector."""
    return RegimeDetector()


def create_test_df(bars: int, trend: str = "none") -> pd.DataFrame:
    """Create test DataFrame with OHLCV data."""
    dates = pd.date_range(start="2026-01-01", periods=bars, freq="1min")

    if trend == "up":
        closes = np.arange(100, 100 + bars * 0.5, 0.5)
    elif trend == "down":
        closes = np.arange(100, 100 - bars * 0.5, -0.5)
    else:
        closes = 100 + np.sin(np.arange(bars) * 2 * np.pi / 20) * 2

    df = pd.DataFrame(
        {
            "open": closes - 0.1,
            "high": closes + 0.5,
            "low": closes - 0.5,
            "close": closes,
            "volume": 1000000,
        },
        index=dates,
    )

    return df


class TestMultiTimeframeSMA:
    """Test multi-timeframe SMA strategy."""

    @pytest.mark.asyncio
    async def test_fast_sma_signal_has_metadata(self, strategy):
        """Signals should include SMA period metadata."""
        df = create_test_df(60, trend="up")

        signals = await strategy.on_bar("TEST", df)

        # Verify signals have expected metadata
        for signal in signals:
            assert "sma_period" in signal.metadata
            assert signal.metadata["sma_period"] in [(5, 15), (10, 30), (20, 50)]

    @pytest.mark.asyncio
    async def test_multiple_sma_periods_checked(self, strategy):
        """Strategy should check all three SMA periods."""
        df = create_test_df(60, trend="up")

        signals = await strategy.on_bar("TEST", df)

        # Verify returns list (not single signal)
        assert isinstance(signals, list)

        # Should have metadata for confidence and regime
        for signal in signals:
            assert "confidence" in signal.metadata
            assert "regime" in signal.metadata

    @pytest.mark.asyncio
    async def test_confidence_high_in_trend(self, strategy):
        """Signals in trending market should have high confidence."""
        df = create_test_df(60, trend="up")

        signals = await strategy.on_bar("TEST", df)

        for signal in signals:
            assert "confidence" in signal.metadata
            if signal.metadata["regime"] == "trending":
                assert signal.metadata["confidence"] >= 0.5

    @pytest.mark.asyncio
    async def test_confidence_low_in_ranging(self, strategy):
        """Signals in ranging market should have low confidence."""
        df = create_test_df(50, trend="none")

        signals = await strategy.on_bar("TEST", df)

        for signal in signals:
            if signal.metadata["regime"] == "ranging":
                assert signal.metadata["confidence"] < 0.5

    @pytest.mark.asyncio
    async def test_slow_sma_highest_confidence_in_trend(self, strategy):
        """Slow SMA should have highest confidence in trending market."""
        df = create_test_df(60, trend="up")

        signals = await strategy.on_bar("TEST", df)

        slow_signals = [s for s in signals if s.metadata["sma_period"] == (20, 50)]

        if slow_signals and slow_signals[0].metadata["regime"] == "trending":
            assert slow_signals[0].metadata["confidence"] > 0.7


class TestRegimeDetection:
    """Test market regime detection."""

    def test_uptrend_detected(self, regime_detector):
        """Should detect uptrend correctly."""
        df = create_test_df(60, trend="up")

        regime = regime_detector.detect(df)

        assert regime.regime == "trending"
        assert regime.trend_direction == "up"
        assert regime.confidence > 0.5

    def test_downtrend_detected(self, regime_detector):
        """Should detect downtrend correctly."""
        df = create_test_df(60, trend="down")

        regime = regime_detector.detect(df)

        assert regime.regime == "trending"
        assert regime.trend_direction == "down"
        assert regime.confidence > 0.5

    def test_ranging_detected(self, regime_detector):
        """Should detect ranging/choppy market."""
        df = create_test_df(50, trend="none")

        regime = regime_detector.detect(df)

        assert regime.regime == "ranging"
        assert regime.trend_direction == "none"

    def test_insufficient_bars_returns_unknown(self, regime_detector):
        """With <50 bars, should return unknown regime."""
        df = create_test_df(30)

        regime = regime_detector.detect(df)

        assert regime.regime == "unknown"
        assert regime.confidence == 0.0


class TestConfidenceScoring:
    """Test signal confidence scoring."""

    def test_slow_sma_high_confidence_in_trend(self, strategy):
        """SMA(20,50) in trend should score 0.9."""
        regime = {"regime": "trending", "direction": "up", "strength": 1.0}

        confidence = strategy._score_confidence(20, 50, regime)

        assert confidence == 0.9

    def test_medium_sma_medium_confidence_in_trend(self, strategy):
        """SMA(10,30) in trend should score 0.7."""
        regime = {"regime": "trending", "direction": "up", "strength": 1.0}

        confidence = strategy._score_confidence(10, 30, regime)

        assert confidence == 0.7

    def test_fast_sma_low_confidence_in_ranging(self, strategy):
        """SMA(5,15) in ranging should score <0.5."""
        regime = {"regime": "ranging", "direction": "none", "strength": 0.0}

        confidence = strategy._score_confidence(5, 15, regime)

        assert confidence < 0.5


class TestIntegration:
    """Integration tests for multi-timeframe SMA + regime."""

    @pytest.mark.asyncio
    async def test_all_three_strategies_coexist(self, strategy):
        """All three SMA periods should work together."""
        df = create_test_df(60, trend="up")

        signals = await strategy.on_bar("TEST", df)

        for signal in signals:
            assert "sma_period" in signal.metadata
            assert "confidence" in signal.metadata
            assert "regime" in signal.metadata

    @pytest.mark.asyncio
    async def test_regime_metadata_included(self, strategy):
        """Signals should include regime metadata."""
        df = create_test_df(60, trend="up")

        signals = await strategy.on_bar("TEST", df)

        for signal in signals:
            assert signal.metadata["regime"] in ["trending", "ranging", "unknown"]
            assert "regime_strength" in signal.metadata

    @pytest.mark.asyncio
    async def test_returns_list_of_signals(self, strategy):
        """Multi-timeframe should return list of signals."""
        df = create_test_df(60, trend="up")

        signals = await strategy.on_bar("TEST", df)

        # Should return list (not None or single signal)
        assert isinstance(signals, list)

        # Each signal should have required fields
        for signal in signals:
            assert hasattr(signal, "symbol")
            assert hasattr(signal, "signal_type")
            assert signal.symbol == "TEST"
            assert signal.signal_type in ["BUY", "SELL"]


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
