"""Snapshot data (bid/ask for spread checks).

Fetches on-demand via market_data API.
Caches briefly to avoid redundant calls.
"""

import logging
from datetime import datetime, timedelta, timezone
from typing import Optional

from src.alpaca_api.market_data import MarketDataClient

logger = logging.getLogger(__name__)


class SnapshotsHandler:
    """Snapshot handler."""
    
    def __init__(self, market_data_client: MarketDataClient, cache_ttl_sec: int = 10) -> None:
        """Initialise snapshots handler.
        
        Args:
            market_data_client: Market data API client
            cache_ttl_sec: Cache time-to-live in seconds
        """
        self.market_data_client = market_data_client
        self.cache_ttl_sec = cache_ttl_sec
        
        # Simple cache: (timestamp, data)
        self.cache: dict[str, tuple[datetime, dict]] = {}
    
    def get_snapshot(self, symbol: str) -> Optional[dict]:
        """Get latest snapshot for symbol.
        
        Uses cache if fresh, otherwise fetches from API.
        
        Args:
            symbol: Stock symbol
        
        Returns:
            Dict with keys: bid, ask, bid_size, ask_size, last_quote_time
        """
        # Check cache
        if symbol in self.cache:
            cached_time, cached_data = self.cache[symbol]
            age = (datetime.now(timezone.utc) - cached_time).total_seconds()
            if age < self.cache_ttl_sec:
                return cached_data
        
        # Fetch fresh
        try:
            data = self.market_data_client.get_snapshot(symbol)
            
            # Cache it
            self.cache[symbol] = (datetime.now(timezone.utc), data)
            
            return data
        except (ConnectionError, TimeoutError) as e:
            logger.error(f"Failed to get snapshot for {symbol}: {e}")
            return None
