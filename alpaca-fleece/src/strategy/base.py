"""Base strategy class."""

from abc import ABC, abstractmethod
from datetime import datetime

import pandas as pd

from src.event_bus import SignalEvent


class BaseStrategy(ABC):
    """Abstract base for all strategies."""
    
    @property
    @abstractmethod
    def name(self) -> str:
        """Strategy name."""
        pass
    
    @abstractmethod
    def get_required_history(self) -> int:
        """Minimum bars needed before first signal."""
        pass
    
    @abstractmethod
    async def on_bar(self, symbol: str, df: pd.DataFrame) -> SignalEvent | None:
        """Process bar and emit signal if triggered.
        
        Args:
            symbol: Stock symbol
            df: DataFrame with bars (index=timestamp, columns=open/high/low/close/volume/etc)
        
        Returns:
            SignalEvent or None
        """
        pass
