"""WebSocket stream - connectivity only (raw passthrough).

This module owns:
- Connecting to Alpaca WebSocket streams
- Reconnect logic with exponential backoff
- Delivering raw SDK objects to DataHandler
- Rate limit protection (HTTP 429 handling)
- Batched subscription (prevent rate limits)

MUST NOT:
- Normalise data into internal events
- Write to SQLite
- Publish to EventBus
"""

import asyncio
import logging
import time
from itertools import islice
from typing import Callable, Optional

from alpaca.data.enums import DataFeed
from alpaca.data.live import StockDataStream
from alpaca.trading.stream import TradingStream

from src.rate_limiter import RateLimiter

logger = logging.getLogger(__name__)


def batch_iter(iterable, batch_size: int):
    """Yield successive batches from iterable.

    Args:
        iterable: Sequence to batch
        batch_size: Size of each batch

    Yields:
        Lists of batch_size items (last batch may be smaller)

    Example:
        >>> list(batch_iter([1,2,3,4,5], 2))
        [[1,2], [3,4], [5]]
    """
    iterator = iter(iterable)
    while batch := list(islice(iterator, batch_size)):
        yield batch


class StreamError(Exception):
    """Raised when stream operation fails."""

    pass


class Stream:
    """WebSocket stream manager (connectivity only)."""

    def __init__(
        self,
        api_key: str,
        secret_key: str,
        paper: bool = True,
        feed: str = "iex",  # "iex" or "sip"
    ) -> None:
        """Initialise stream.

        Args:
            api_key: Alpaca API key
            secret_key: Alpaca secret key
            paper: True for paper trading
            feed: "iex" (free) or "sip" (paid)
        """
        self.api_key = api_key
        self.secret_key = secret_key
        self.paper = paper
        # Convert string feed to enum
        if isinstance(feed, str):
            self.feed = DataFeed.IEX if feed.lower() == "iex" else DataFeed.SIP
        else:
            self.feed = feed

        # Streams (lazy init)
        self.market_data_stream: Optional[StockDataStream] = None
        self.trade_updates_stream: Optional[TradingStream] = None

        # Callbacks (provided by DataHandler)
        self.on_bar: Optional[Callable] = None
        self.on_order_update: Optional[Callable] = None
        self.on_market_disconnect: Optional[Callable] = None
        self.on_trade_disconnect: Optional[Callable] = None

        # State
        self.market_connected = False
        self.trade_connected = False
        self.reconnect_attempts = 0
        self.max_reconnect_attempts = 10

        # Rate limit protection (Alpaca's HTTP 429 handling)
        self.market_rate_limiter = RateLimiter(
            base_delay=2.0,  # Start with 2s backoff
            max_delay=120.0,  # Cap at 2 minutes
            max_retries=5,  # Give up after 5 failures
        )
        self.trade_rate_limiter = RateLimiter(
            base_delay=2.0,
            max_delay=120.0,
            max_retries=5,
        )

    def register_handlers(
        self,
        on_bar: Callable,
        on_order_update: Callable,
        on_market_disconnect: Callable,
        on_trade_disconnect: Callable,
    ) -> None:
        """Register callbacks from DataHandler.

        Args:
            on_bar: Called with raw bar data (SDK object)
            on_order_update: Called with raw order update (SDK object)
            on_market_disconnect: Called when market stream disconnects
            on_trade_disconnect: Called when trade stream disconnects
        """
        self.on_bar = on_bar
        self.on_order_update = on_order_update
        self.on_market_disconnect = on_market_disconnect
        self.on_trade_disconnect = on_trade_disconnect

    async def start(self, symbols: list[str]) -> None:
        """Start both streams.

        Args:
            symbols: List of symbols to subscribe to
        """
        try:
            # Start market data stream
            await self._start_market_stream(symbols)

            # Start trade updates stream
            await self._start_trade_stream()
        except Exception as e:
            raise StreamError(f"Failed to start streams: {e}")

    async def _start_market_stream(
        self, symbols: list[str], batch_size: int = 10, batch_delay: float = 1.0
    ) -> None:
        """Start market data stream with batched subscriptions.

        Args:
            symbols: List of symbols to subscribe to
            batch_size: Number of symbols per batch (default: 10)
            batch_delay: Delay in seconds between batches (default: 1.0)
        """
        self.market_data_stream = StockDataStream(
            api_key=self.api_key,
            secret_key=self.secret_key,
            feed=self.feed,
        )

        # Register bar handler (raw passthrough)
        async def handle_bar(bar):
            if self.on_bar:
                await self.on_bar(bar)

        # Subscribe in batches to avoid rate limits
        logger.info(f"Subscribing to {len(symbols)} symbols in batches of {batch_size}")
        num_batches = (len(symbols) + batch_size - 1) // batch_size
        for i, batch in enumerate(batch_iter(symbols, batch_size)):
            logger.info(f"Subscribing batch {i+1}: {batch}")
            self.market_data_stream.subscribe_bars(handle_bar, *batch)

            # Delay between batches (except after last batch)
            if i < num_batches - 1:
                await asyncio.sleep(batch_delay)

        # Start stream using native async _run_forever() instead of sync run()
        # This avoids the asyncio.run() conflict
        asyncio.create_task(self.market_data_stream._run_forever())
        self.market_connected = True
        logger.info(
            f"Market stream connected: {len(symbols)} symbols in {(len(symbols)-1)//batch_size + 1} batches"
        )

    async def _start_trade_stream(self) -> None:
        """Start trade updates stream."""
        self.trade_updates_stream = TradingStream(
            api_key=self.api_key,
            secret_key=self.secret_key,
        )

        # Register order update handler (raw passthrough)
        async def handle_trade_update(update):
            if self.on_order_update:
                await self.on_order_update(update)

        self.trade_updates_stream.subscribe_trade_updates(handle_trade_update)

        # Start stream using native async _run_forever() instead of sync run()
        asyncio.create_task(self.trade_updates_stream._run_forever())
        self.trade_connected = True
        logger.info("Trade stream connected")

    async def stop(self) -> None:
        """Stop both streams."""
        if self.market_data_stream:
            await self.market_data_stream.close()
            self.market_connected = False

        if self.trade_updates_stream:
            await self.trade_updates_stream.close()
            self.trade_connected = False

        logger.info("Streams stopped")

    async def reconnect_market_stream(self, symbols: list[str]) -> bool:
        """Attempt to reconnect market stream with rate limit protection.

        Args:
            symbols: Symbols to resubscribe

        Returns:
            True if reconnected, False if max attempts exceeded
        """
        # Check if rate limited
        if self.market_rate_limiter.is_limited:
            logger.error("Market stream: Rate limit exceeded (HTTP 429), giving up")
            if self.on_market_disconnect:
                await self.on_market_disconnect()
            return False

        # Wait if in backoff period
        if not self.market_rate_limiter.is_ready_to_retry():
            backoff = self.market_rate_limiter.get_backoff_delay()
            elapsed = time.time() - self.market_rate_limiter.last_failure_time
            remaining = backoff - elapsed
            logger.warning(f"Market stream: Rate limited, waiting {remaining:.1f}s before retry")
            await asyncio.sleep(remaining)

        try:
            await self._start_market_stream(symbols)
            self.market_rate_limiter.record_success()
            logger.info("Market stream reconnected (rate limiter reset)")
            return True
        except ValueError as e:
            if "429" in str(e) or "connection limit" in str(e).lower():
                logger.warning("Market stream: HTTP 429 rate limit hit")
                self.market_rate_limiter.record_failure()
                # Don't immediately retry, let backoff kick in
                return False
            else:
                logger.error(f"Market stream reconnect failed: {e}")
                self.market_rate_limiter.record_failure()
                return False
        except Exception as e:
            logger.error(f"Market stream reconnect failed: {e}")
            self.market_rate_limiter.record_failure()
            return False
