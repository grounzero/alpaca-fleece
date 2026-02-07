"""Market regime detection (trending vs ranging)."""

from dataclasses import dataclass

import pandas as pd
import pandas_ta as ta


@dataclass
class RegimeScore:
    """Market regime analysis result."""

    regime: str  # "trending" | "ranging" | "unknown"
    confidence: float  # 0.0-1.0
    trend_direction: str  # "up" | "down" | "none"
    strength: float  # 0.0-1.0 (trend strength or range tightness)


class RegimeDetector:
    """Detects market regime to guide trading strategy."""

    def detect(self, df: pd.DataFrame) -> RegimeScore:
        """Analyze price action to determine regime.

        Args:
            df: DataFrame with OHLCV data

        Returns:
            RegimeScore with regime assessment
        """
        if len(df) < 50:
            return RegimeScore("unknown", 0.0, "none", 0.0)

        close = df["close"].iloc[-1]
        ta.sma(df["close"], length=20).iloc[-1]
        sma_50 = ta.sma(df["close"], length=50).iloc[-1]
        atr = ta.atr(df["high"], df["low"], df["close"], length=14).iloc[-1]

        # Trend strength: distance from slow SMA / ATR
        distance = close - sma_50
        trend_strength = abs(distance) / atr if atr > 0 else 0

        # Normalize trend strength to 0.0-1.0
        normalized_strength = min(trend_strength / 2.0, 1.0)

        # Detect regime
        if trend_strength > 1.5:
            # Strong trend
            direction = "up" if distance > 0 else "down"
            return RegimeScore(
                regime="trending",
                confidence=0.9,
                trend_direction=direction,
                strength=normalized_strength,
            )
        elif trend_strength > 0.8:
            # Weak trend
            direction = "up" if distance > 0 else "down"
            return RegimeScore(
                regime="trending",
                confidence=0.6,
                trend_direction=direction,
                strength=normalized_strength,
            )
        elif trend_strength < 0.5:
            # Ranging/choppy market
            return RegimeScore(
                regime="ranging",
                confidence=0.8,
                trend_direction="none",
                strength=0.0,
            )
        else:
            # Transitional/unknown
            return RegimeScore(
                regime="unknown",
                confidence=0.5,
                trend_direction="none",
                strength=normalized_strength,
            )
