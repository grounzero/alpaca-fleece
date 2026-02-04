"""SMA Crossover strategy."""
from typing import Optional
import pandas as pd
import pandas_ta as ta

from src.strategy.base import BaseStrategy
from src.event_bus import SignalEvent
from src.state_store import StateStore


class SMACrossoverStrategy(BaseStrategy):
    """Simple Moving Average crossover strategy."""

    def __init__(self, fast_period: int, slow_period: int, state_store: StateStore):
        """
        Initialize SMA crossover strategy.

        Args:
            fast_period: Fast SMA period
            slow_period: Slow SMA period
            state_store: State store for tracking last signals
        """
        super().__init__(name="SMA_Crossover")
        self.fast_period = fast_period
        self.slow_period = slow_period
        self.state_store = state_store

    def get_required_history(self) -> int:
        """Get minimum bars required."""
        return self.slow_period + 2  # Need extra bars for crossover detection

    def on_bar(self, symbol: str, df: pd.DataFrame) -> Optional[SignalEvent]:
        """
        Generate signal on SMA crossover.

        Only emits signals on crossover events, not on every bar where fast > slow.
        Also prevents duplicate consecutive signals for the same symbol.

        Args:
            symbol: Stock symbol
            df: DataFrame with OHLCV data

        Returns:
            SignalEvent on crossover, None otherwise
        """
        if len(df) < self.get_required_history():
            return None

        # Calculate SMAs
        df = df.copy()
        df["sma_fast"] = ta.sma(df["close"], length=self.fast_period)
        df["sma_slow"] = ta.sma(df["close"], length=self.slow_period)

        # Drop NaN values
        df = df.dropna()

        if len(df) < 2:
            return None

        # Get last two rows to detect crossover
        current = df.iloc[-1]
        previous = df.iloc[-2]

        fast_current = current["sma_fast"]
        slow_current = current["sma_slow"]
        fast_previous = previous["sma_fast"]
        slow_previous = previous["sma_slow"]

        # Get last signal for this symbol
        last_signal = self.state_store.get_last_signal(symbol)

        # Detect bullish crossover (fast crosses above slow)
        if fast_previous <= slow_previous and fast_current > slow_current:
            # Only emit if last signal wasn't BUY
            if last_signal != "BUY":
                signal = SignalEvent(
                    symbol=symbol,
                    side="buy",
                    strategy_name=self.name,
                    signal_timestamp=current.name,  # Index is timestamp
                    metadata={
                        "sma_fast": float(fast_current),
                        "sma_slow": float(slow_current),
                        "close": float(current["close"]),
                    },
                )

                # Update last signal
                self.state_store.set_last_signal(symbol, "BUY")

                return signal

        # Detect bearish crossover (fast crosses below slow)
        elif fast_previous >= slow_previous and fast_current < slow_current:
            # Only emit if last signal wasn't SELL
            if last_signal != "SELL":
                signal = SignalEvent(
                    symbol=symbol,
                    side="sell",
                    strategy_name=self.name,
                    signal_timestamp=current.name,  # Index is timestamp
                    metadata={
                        "sma_fast": float(fast_current),
                        "sma_slow": float(slow_current),
                        "close": float(current["close"]),
                    },
                )

                # Update last signal
                self.state_store.set_last_signal(symbol, "SELL")

                return signal

        return None
