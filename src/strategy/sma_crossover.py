"""SMA Crossover strategy - Multi-timeframe with regime detection."""

from typing import Any, Optional

import numpy as np
import pandas as pd
import pandas_ta as ta

from src.event_bus import SignalEvent
<<<<<<< HEAD
from src.state_store import StateStore
=======
>>>>>>> 7e787d8 (Clean trading bot implementation)
from src.strategy.base import BaseStrategy


class SMACrossover(BaseStrategy):
    """Multi-timeframe SMA crossover strategy with regime detection.

    Emits BUY/SELL signals from 3 independent SMA pairs:
    - Fast (5, 15): Quick scalp trades
    - Medium (10, 30): Baseline (original)
    - Slow (20, 50): High-confidence trend trades

    Signals include confidence scores based on market regime:
    - Trending: Use slow SMA (0.8-0.9 confidence)
    - Ranging: Low confidence (0.2-0.4)
    - Unknown: Medium confidence (0.5-0.7)
    """

<<<<<<< HEAD
    def __init__(self, state_store: StateStore, crypto_symbols: Optional[list[str]] = None) -> None:
        """Initialise multi-timeframe strategy.

        Args:
            state_store: State store for tracking last signals
            crypto_symbols: List of crypto symbols for warmup detection (Hybrid)
        """
        self.state_store = state_store
=======
    def __init__(self, crypto_symbols: Optional[list[str]] = None) -> None:
        """Initialise multi-timeframe strategy.

        Args:
            crypto_symbols: List of crypto symbols for warmup detection (Hybrid)
        """
>>>>>>> 7e787d8 (Clean trading bot implementation)
        self.crypto_symbols = crypto_symbols or []  # Hybrid: crypto symbol list

        # Define SMA periods (fast, slow) for each strategy
        self.periods = [
            (5, 15),  # Fast scalp
            (10, 30),  # Medium (baseline)
            (20, 50),  # Slow (high confidence)
        ]

    @property
    def name(self) -> str:
        """Strategy name."""
        return "sma_crossover_multi"

    def get_required_history(self, symbol: Optional[str] = None) -> int:
        """Minimum bars needed (symbol-aware for Hybrid crypto support).

        Args:
            symbol: Stock or crypto symbol (for Hybrid detection)

        Returns:
            Minimum bars for SMA(20,50) warmup
        """
        # Crypto (BTCUSD/ETHUSD): Can trade 24/5, more data available
        # Use same as equities for consistency
        return 51  # 50 for SMA(20,50) + 1 for crossover

    async def on_bar(self, symbol: str, df: pd.DataFrame) -> list[SignalEvent]:
        """Process bar and emit all valid signals with confidence.

        Args:
            symbol: Stock symbol
            df: DataFrame with bars

        Returns:
            List of SignalEvent objects (0-3 per bar)
        """
        if len(df) < self.get_required_history():
            return []

        signals: list[SignalEvent] = []
        regime = self._detect_regime(df)

        # Check all SMA pairs
        for fast_period, slow_period in self.periods:
            signal = self._check_crossover(symbol, df, fast_period, slow_period)

            if signal:
                # Score confidence based on regime and SMA period
                confidence = self._score_confidence(fast_period, slow_period, regime)
                signal.metadata["confidence"] = confidence
                signal.metadata["regime"] = regime["regime"]
                signal.metadata["regime_strength"] = regime["strength"]
                signals.append(signal)

        return signals

    def _check_crossover(
        self, symbol: str, df: pd.DataFrame, fast: int, slow: int
    ) -> SignalEvent | None:
        """Check for SMA crossover on given periods.

        Args:
            symbol: Stock symbol
            df: DataFrame with bars
            fast: Fast SMA period
            slow: Slow SMA period

        Returns:
            SignalEvent or None
        """
        fast_sma = ta.sma(df["close"], length=fast)
        slow_sma = ta.sma(df["close"], length=slow)

        if fast_sma is None or slow_sma is None or len(fast_sma) < 2:
            return None

        fast_now = fast_sma.iloc[-1]
        fast_prev = fast_sma.iloc[-2]
        slow_now = slow_sma.iloc[-1]
        slow_prev = slow_sma.iloc[-2]

        # Detect crossover
        upward_cross = fast_prev <= slow_prev and fast_now > slow_now
        downward_cross = fast_prev >= slow_prev and fast_now < slow_now

        if not (upward_cross or downward_cross):
            return None

        signal_type = "BUY" if upward_cross else "SELL"

<<<<<<< HEAD
        # Prevent duplicate consecutive signals per SMA period (Win #3: persisted)
        last_signal = self.state_store.get_last_signal(symbol, (fast, slow))  # Win #3: from DB

        if last_signal == signal_type:
            return None  # Duplicate, skip

        self.state_store.save_last_signal(symbol, signal_type, (fast, slow))  # Win #3: persisted

        return SignalEvent(
            symbol=symbol,
            signal_type=signal_type,
            timestamp=df.index[-1],
            metadata={
                "sma_period": (fast, slow),
                "fast_sma": float(fast_now),
                "slow_sma": float(slow_now),
                "close": float(df["close"].iloc[-1]),
            },
        )

    def _detect_regime(self, df: pd.DataFrame) -> dict[str, Any]:
        """Detect market regime: trending vs ranging.

        Args:
            df: DataFrame with bars

        Returns:
            Dict with keys: regime, strength, direction
        """
        if len(df) < 50:
            return {"regime": "unknown", "strength": 0.0, "direction": "none"}

        close = df["close"].iloc[-1]
        sma_50_series = ta.sma(df["close"], length=50)
        atr_series = ta.atr(df["high"], df["low"], df["close"], length=14)

        if sma_50_series is None or atr_series is None:
            return {"regime": "unknown", "strength": 0.0, "direction": "none"}

        sma_50 = sma_50_series.iloc[-1]
        atr = atr_series.iloc[-1]

=======
        # NOTE: Deduplication moved to OrderManager.submit_signal() to separate concerns.
        # Strategy returns all crossover signals; OrderManager filters duplicates based on
        # last submitted signal state (persisted in StateStore).

        return SignalEvent(
            symbol=symbol,
            signal_type=signal_type,
            timestamp=df.index[-1],
            metadata={
                "sma_period": (fast, slow),
                "fast_sma": float(fast_now),
                "slow_sma": float(slow_now),
                "close": float(df["close"].iloc[-1]),
            },
        )

    def _detect_regime(self, df: pd.DataFrame) -> dict[str, Any]:
        """Detect market regime: trending vs ranging.

        Args:
            df: DataFrame with bars

        Returns:
            Dict with keys: regime, strength, direction
        """
        if len(df) < 50:
            return {"regime": "unknown", "strength": 0.0, "direction": "none"}

        close = df["close"].iloc[-1]
        sma_50_series = ta.sma(df["close"], length=50)
        atr_series = ta.atr(df["high"], df["low"], df["close"], length=14)

        if sma_50_series is None or atr_series is None:
            return {"regime": "unknown", "strength": 0.0, "direction": "none"}

        sma_50 = sma_50_series.iloc[-1]
        atr = atr_series.iloc[-1]

>>>>>>> 7e787d8 (Clean trading bot implementation)
        # Trend strength: distance from slow SMA / ATR
        distance = close - sma_50
        if atr == 0 or not np.isfinite(atr):
            trend_strength = 0.0  # Unknown/ranging regime
        else:
            trend_strength = min(abs(distance) / atr, 2.0)  # Cap at 2.0

        # Detect regime
        if trend_strength > 1.5:
            # Strong trend
            direction = "up" if distance > 0 else "down"
            return {
                "regime": "trending",
                "strength": min(trend_strength / 2.0, 1.0),
                "direction": direction,
            }
        elif trend_strength < 0.5:
            # Ranging/choppy
            return {"regime": "ranging", "strength": 0.0, "direction": "none"}
        else:
            # Transitional/unknown
            return {
                "regime": "unknown",
                "strength": trend_strength / 2.0,
                "direction": "none",
            }

    def _score_confidence(self, fast: int, slow: int, regime: dict[str, Any]) -> float:
        """Score signal confidence (0.0-1.0) based on SMA period and regime.

        Args:
            fast: Fast SMA period
            slow: Slow SMA period
            regime: Regime dict from _detect_regime

        Returns:
            Confidence score 0.0-1.0
        """
        regime_type = regime["regime"]

        # Confidence based on regime
        if regime_type == "trending":
            # In trends, slow SMA is most reliable
            if (fast, slow) == (20, 50):
                return 0.9  # Highest confidence
            elif (fast, slow) == (10, 30):
                return 0.7  # Good confidence
            else:  # (5, 15)
                return 0.5  # Medium confidence

        elif regime_type == "ranging":
            # In choppy markets, all SMAs are unreliable
            # Return low confidence to filter these trades
            if (fast, slow) == (20, 50):
                return 0.3  # Even slow SMA is unreliable
            else:
                return 0.2  # Very unreliable

        else:  # unknown/transitional
            # Medium confidence in unknown regimes
            if (fast, slow) == (20, 50):
                return 0.7
            elif (fast, slow) == (10, 30):
                return 0.6
            else:
                return 0.5
