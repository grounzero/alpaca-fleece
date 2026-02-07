"""Base strategy class."""

from abc import ABC, abstractmethod
from datetime import datetime
from typing import List

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
    def get_required_history(self, symbol: str | None = None) -> int:
        """Minimum bars needed before first signal.
        
        Args:
            symbol: Optional stock symbol for symbol-specific history requirements
        
        Returns:
            Number of bars required
        """
        pass
    
    @abstractmethod
    async def on_bar(self, symbol: str, df: pd.DataFrame) -> List[SignalEvent]:
        """Process bar and emit signals if triggered.
        
        Args:
            symbol: Stock symbol
            df: DataFrame with bars (index=timestamp, columns=open/high/low/close/volume/etc)
        
        Returns:
            List of SignalEvent instances (empty if no signal)
        """
        pass
