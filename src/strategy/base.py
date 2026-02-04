"""Base strategy interface."""
from abc import ABC, abstractmethod
from typing import Optional
import pandas as pd

from src.event_bus import SignalEvent


class BaseStrategy(ABC):
    """Abstract base class for trading strategies."""

    def __init__(self, name: str):
        """
        Initialize strategy.

        Args:
            name: Strategy name
        """
        self.name = name

    @abstractmethod
    def get_required_history(self) -> int:
        """
        Get minimum number of bars required for strategy.

        Returns:
            Minimum number of bars needed
        """
        pass

    @abstractmethod
    def on_bar(self, symbol: str, df: pd.DataFrame) -> Optional[SignalEvent]:
        """
        Process new bar and generate signal if applicable.

        Args:
            symbol: Stock symbol
            df: DataFrame with OHLCV data (indexed by timestamp)

        Returns:
            SignalEvent if signal generated, None otherwise
        """
        pass

    def __repr__(self) -> str:
        """String representation."""
        return f"{self.__class__.__name__}(name={self.name})"
