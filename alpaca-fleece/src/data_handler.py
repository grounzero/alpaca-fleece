"""Data handler - coordination layer over data/* handlers.

Routes raw data from Stream to appropriate data/* handlers.
Does NOT normalise, persist, or publish (handlers do that).

Provides query interface:
- get_dataframe(symbol) → DataFrame
- has_sufficient_history(symbol) → bool
- get_snapshot(symbol) → dict
"""

import logging
from typing import Optional

import pandas as pd

from src.alpaca_api.market_data import MarketDataClient
from src.data.bars import BarsHandler
from src.data.snapshots import SnapshotsHandler
from src.data.order_updates import OrderUpdatesHandler
from src.event_bus import EventBus
from src.state_store import StateStore

logger = logging.getLogger(__name__)


class DataHandler:
    """Coordination layer over data/* handlers."""
    
    def __init__(
        self,
        state_store: StateStore,
        event_bus: EventBus,
        market_data_client: MarketDataClient,
    ) -> None:
        """Initialise data handler.
        
        Args:
            state_store: SQLite state store
            event_bus: Event bus
            market_data_client: Market data API client
        """
        self.state_store = state_store
        self.event_bus = event_bus
        self.market_data_client = market_data_client
        
        # Handlers
        self.bars = BarsHandler(state_store, event_bus, market_data_client)
        self.snapshots = SnapshotsHandler(market_data_client)
        self.order_updates = OrderUpdatesHandler(state_store, event_bus)
    
    async def on_bar(self, raw_bar) -> None:
        """Route raw bar to bars handler.
        
        Args:
            raw_bar: Raw bar from Stream
        """
        await self.bars.on_bar(raw_bar)
    
    async def on_order_update(self, raw_update) -> None:
        """Route raw order update to order_updates handler.
        
        Args:
            raw_update: Raw order update from Stream
        """
        await self.order_updates.on_order_update(raw_update)
    
    def get_dataframe(self, symbol: str) -> Optional[pd.DataFrame]:
        """Get bars as DataFrame for strategy.
        
        Args:
            symbol: Stock symbol
        
        Returns:
            DataFrame or None
        """
        return self.bars.get_dataframe(symbol)
    
    def has_sufficient_history(self, symbol: str, min_bars: int = 50) -> bool:
        """Check if enough history for strategy.
        
        Args:
            symbol: Stock symbol
            min_bars: Minimum bars required
        
        Returns:
            True if sufficient
        """
        return self.bars.has_sufficient_history(symbol, min_bars)
    
    def get_snapshot(self, symbol: str) -> Optional[dict]:
        """Get latest snapshot for spread checks.
        
        Args:
            symbol: Stock symbol
        
        Returns:
            Snapshot dict or None
        """
        return self.snapshots.get_snapshot(symbol)
    
    async def backfill_bars(self, symbol: str, timeframe: str = "1Min", limit: int = 100) -> None:
        """Backfill bars after stream reconnect.
        
        Args:
            symbol: Stock symbol
            timeframe: Bar timeframe
            limit: Number of bars
        """
        await self.bars.backfill(symbol, timeframe, limit)
