"""Bar data normalisation, persistence, caching.

Receives raw bars from Stream via DataHandler.
Normalises to BarEvent.
Persists to SQLite.
Publishes to EventBus.
Handles backfill on stream reconnect.
"""

import logging
from collections import deque
from typing import Optional

import pandas as pd

from src.alpaca_api.market_data import MarketDataClient
from src.event_bus import BarEvent, EventBus
from src.state_store import StateStore

logger = logging.getLogger(__name__)


class BarsHandler:
    """Bar data handler."""
    
    def __init__(
        self,
        state_store: StateStore,
        event_bus: EventBus,
        market_data_client: MarketDataClient,
        history_size: int = 100,
    ) -> None:
        """Initialise bars handler.
        
        Args:
            state_store: SQLite state store
            event_bus: Event bus for publishing
            market_data_client: Market data API client
            history_size: Number of bars to keep in memory per symbol
        """
        self.state_store = state_store
        self.event_bus = event_bus
        self.market_data_client = market_data_client
        self.history_size = history_size
        
        # In-memory rolling window per symbol
        self.bars_deque: dict[str, deque] = {}
    
    async def on_bar(self, raw_bar) -> None:
        """Process raw bar from stream.
        
        Args:
            raw_bar: Raw bar object from SDK
        
        Raises:
            ValueError: If bar normalization fails
        """
        try:
            # Normalise to BarEvent
            event = self._normalise_bar(raw_bar)
            
            # Persist to SQLite
            self._persist_bar(event)
            
            # Update rolling window
            symbol = event.symbol
            if symbol not in self.bars_deque:
                self.bars_deque[symbol] = deque(maxlen=self.history_size)
            self.bars_deque[symbol].append(event)
            
            # Publish to EventBus
            await self.event_bus.publish(event)
            
            logger.debug(f"Bar: {event.symbol} {event.close}")
        except ValueError as e:
            # Log normalization errors with context
            logger.error(
                f"Failed to normalize bar: {e}",
                extra={"raw_bar": str(raw_bar)},
            )
            raise
        except (TypeError, AttributeError) as e:
            # Log data-related exceptions with context and re-raise
            logger.error(
                f"Unexpected error processing bar for {getattr(raw_bar, 'symbol', 'UNKNOWN')}: {e}",
                extra={"error_type": type(e).__name__},
            )
            raise
    
    def _normalise_bar(self, raw_bar) -> BarEvent:
        """Normalise raw SDK bar to BarEvent."""
        return BarEvent(
            symbol=raw_bar.symbol,
            timestamp=raw_bar.timestamp,
            open=float(raw_bar.open),
            high=float(raw_bar.high),
            low=float(raw_bar.low),
            close=float(raw_bar.close),
            volume=int(raw_bar.volume),
            trade_count=int(raw_bar.trade_count) if hasattr(raw_bar, "trade_count") else None,
            vwap=float(raw_bar.vwap) if hasattr(raw_bar, "vwap") and raw_bar.vwap else None,
        )
    
    def _persist_bar(self, event: BarEvent) -> None:
        """Persist bar to SQLite."""
        import sqlite3
        
        conn = sqlite3.connect(self.state_store.db_path)
        cursor = conn.cursor()
        
        cursor.execute(
            """INSERT OR REPLACE INTO bars 
               (symbol, timeframe, timestamp_utc, open, high, low, close, volume, trade_count, vwap)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (
                event.symbol,
                "1Min",  # TODO: make configurable
                event.timestamp.isoformat(),
                event.open,
                event.high,
                event.low,
                event.close,
                event.volume,
                event.trade_count,
                event.vwap,
            ),
        )
        conn.commit()
        conn.close()
    
    def get_dataframe(self, symbol: str) -> Optional[pd.DataFrame]:
        """Get bars for symbol as DataFrame.
        
        Args:
            symbol: Stock symbol
        
        Returns:
            DataFrame with bars, or None if no data
        """
        if symbol not in self.bars_deque or len(self.bars_deque[symbol]) == 0:
            return None
        
        bars = list(self.bars_deque[symbol])
        df = pd.DataFrame([
            {
                "open": b.open,
                "high": b.high,
                "low": b.low,
                "close": b.close,
                "volume": b.volume,
                "trade_count": b.trade_count,
                "vwap": b.vwap,
            }
            for b in bars
        ])
        df.index = pd.DatetimeIndex([b.timestamp for b in bars], name="timestamp")
        return df
    
    def has_sufficient_history(self, symbol: str, min_bars: int = 50) -> bool:
        """Check if we have enough history for strategy.
        
        Args:
            symbol: Stock symbol
            min_bars: Minimum bars required
        
        Returns:
            True if sufficient history exists
        """
        return symbol in self.bars_deque and len(self.bars_deque[symbol]) >= min_bars
    
    async def backfill(self, symbol: str, timeframe: str = "1Min", limit: int = 100) -> None:
        """Backfill bars after stream reconnect.
        
        Args:
            symbol: Stock symbol
            timeframe: Bar timeframe
            limit: Number of bars to fetch
        """
        try:
            df = self.market_data_client.get_bars(
                symbol=symbol,
                timeframe=timeframe,
                limit=limit,
            )
            
            if df.empty:
                logger.warning(f"Backfill returned no bars for {symbol}")
                return
            
            # Convert to BarEvents and process
            for _, row in df.iterrows():
                event = BarEvent(
                    symbol=symbol,
                    timestamp=row.name,  # index is timestamp
                    open=float(row["open"]),
                    high=float(row["high"]),
                    low=float(row["low"]),
                    close=float(row["close"]),
                    volume=int(row["volume"]),
                    trade_count=int(row.get("trade_count", 0)) if "trade_count" in row else None,
                    vwap=float(row.get("vwap")) if "vwap" in row else None,
                )
                
                # Update rolling window and SQLite (but don't re-publish to avoid duplicates)
                if symbol not in self.bars_deque:
                    self.bars_deque[symbol] = deque(maxlen=self.history_size)
                self.bars_deque[symbol].append(event)
                self._persist_bar(event)
            
            logger.info(f"Backfilled {len(df)} bars for {symbol}")
        except (ConnectionError, TimeoutError) as e:
            logger.error(f"Backfill failed for {symbol}: {e}")
