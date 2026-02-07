"""Data handler for maintaining rolling windows of market data."""
from collections import deque
from datetime import datetime
from typing import Dict, Optional
import pandas as pd
from src.event_bus import MarketBarEvent


class DataHandler:
    """Maintain rolling windows of bar data per symbol."""

    def __init__(self, window_size: int):
        """
        Set up data handler.

        Args:
            window_size: Maximum number of bars to keep per symbol
        """
        self.window_size = window_size
        self.data: Dict[str, deque] = {}

    def on_bar(self, event: MarketBarEvent) -> Optional[pd.DataFrame]:
        """
        Process incoming bar and return DataFrame if enough history exists.

        Args:
            event: Market bar event

        Returns:
            DataFrame with sufficient history, or None if not enough data yet
        """
        symbol = event.symbol

        # Set up deque for symbol if needed
        if symbol not in self.data:
            self.data[symbol] = deque(maxlen=self.window_size)

        # Append bar data
        bar_dict = {
            "timestamp": event.bar_timestamp,
            "open": event.open,
            "high": event.high,
            "low": event.low,
            "close": event.close,
            "volume": event.volume,
        }

        if event.vwap is not None:
            bar_dict["vwap"] = event.vwap

        self.data[symbol].append(bar_dict)

        # Return DataFrame if we have data
        if len(self.data[symbol]) > 0:
            return self.get_dataframe(symbol)

        return None

    def get_dataframe(self, symbol: str) -> Optional[pd.DataFrame]:
        """
        Get DataFrame for symbol.

        Args:
            symbol: Stock symbol

        Returns:
            DataFrame with bar data, or None if no data exists
        """
        if symbol not in self.data or len(self.data[symbol]) == 0:
            return None

        df = pd.DataFrame(list(self.data[symbol]))
        df.set_index("timestamp", inplace=True)
        df.sort_index(inplace=True)

        return df

    def get_bar_count(self, symbol: str) -> int:
        """Get number of bars stored for symbol."""
        return len(self.data.get(symbol, []))

    def has_sufficient_data(self, symbol: str, required_bars: int) -> bool:
        """Check if symbol has sufficient bars for analysis."""
        return self.get_bar_count(symbol) >= required_bars

    def clear_symbol(self, symbol: str):
        """Clear data for a specific symbol."""
        if symbol in self.data:
            del self.data[symbol]

    def clear_all(self):
        """Clear all data."""
        self.data.clear()

    def add_historical_bars(self, symbol: str, bars: pd.DataFrame):
        """
        Add historical bars (e.g., after reconnect backfill).

        Args:
            symbol: Stock symbol
            bars: DataFrame with OHLCV data, indexed by timestamp
        """
        if symbol not in self.data:
            self.data[symbol] = deque(maxlen=self.window_size)

        # Convert DataFrame rows to dicts and add to deque
        for timestamp, row in bars.iterrows():
            bar_dict = {
                "timestamp": timestamp,
                "open": row.get("open"),
                "high": row.get("high"),
                "low": row.get("low"),
                "close": row.get("close"),
                "volume": row.get("volume"),
            }

            if "vwap" in row:
                bar_dict["vwap"] = row["vwap"]

            # Only add if not already present (avoid duplicates)
            # Check last few bars to see if timestamp already exists
            if not any(b["timestamp"] == timestamp for b in list(self.data[symbol])[-10:]):
                self.data[symbol].append(bar_dict)
