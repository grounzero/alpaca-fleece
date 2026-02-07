"""HTTP polling stream - fallback when WebSocket connection limit exceeded.

This module provides the same interface as Stream but uses HTTP polling instead of WebSocket.
"""

import asyncio
import logging
from datetime import datetime, timedelta, timezone
from typing import Callable, List, Optional

from alpaca.data.timeframe import TimeFrame
from alpaca.data.requests import StockBarsRequest
from alpaca.data.historical import StockHistoricalDataClient

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
    from itertools import islice

    iterator = iter(iterable)
    while batch := list(islice(iterator, batch_size)):
        yield batch


class PollingBar:
    """Minimal bar object matching alpaca Bar interface."""

    def __init__(self, symbol: str, bar) -> None:
        """Wrap alpaca bar."""
        self.symbol = symbol
        self.timestamp = bar.timestamp
        self.open = bar.open
        self.high = bar.high
        self.low = bar.low
        self.close = bar.close
        self.volume = bar.volume
        self.vwap = getattr(bar, "vwap", None)
        self.trade_count = getattr(bar, "trade_count", None)


class StreamPolling:
    """HTTP polling-based stream (fallback for WebSocket connection limits)."""

    def __init__(
        self,
        api_key: str,
        secret_key: str,
        paper: bool = True,
        feed: str = "iex",
        batch_size: int = 25,
    ) -> None:
        """Initialise polling stream.

        Args:
            api_key: Alpaca API key
            secret_key: Alpaca secret key
            paper: True for paper trading
            feed: "iex" (free) or "sip" (paid)
            batch_size: Number of symbols per batch request (default: 25)
        """
        self.api_key = api_key
        self.secret_key = secret_key
        self.paper = paper
        self.feed = feed
        self.batch_size = batch_size

        # Historical data client for polling
        self.client = StockHistoricalDataClient(api_key, secret_key)

        self.market_connected = False
        self.trade_connected = False
        self.on_bar: Optional[Callable] = None
        self.on_order_update: Optional[Callable] = None
        self.on_market_disconnect: Optional[Callable] = None
        self.on_trade_disconnect: Optional[Callable] = None

        self._last_bars = {}  # Track last known bar timestamp per symbol
        self._polling_task = None
        self._symbols = []

    def register_handlers(
        self,
        on_bar: Optional[Callable] = None,
        on_order_update: Optional[Callable] = None,
        on_market_disconnect: Optional[Callable] = None,
        on_trade_disconnect: Optional[Callable] = None,
    ) -> None:
        """Register event handlers."""
        self.on_bar = on_bar
        self.on_order_update = on_order_update
        self.on_market_disconnect = on_market_disconnect
        self.on_trade_disconnect = on_trade_disconnect

    async def start(self, symbols: list[str]) -> None:
        """Start polling stream.

        Args:
            symbols: List of symbols to poll
        """
        self._symbols = symbols
        self.market_connected = True
        self.trade_connected = True

        num_batches = (len(symbols) + self.batch_size - 1) // self.batch_size
        logger.info(
            f"Polling stream started for {len(symbols)} symbols "
            f"in {num_batches} batch(es) of {self.batch_size} (1-min polling)"
        )

        # Start polling task
        self._polling_task = asyncio.create_task(self._poll_loop())

    async def _poll_loop(self) -> None:
        """Poll for new bars every minute."""
        logger.info("Polling loop started")
        try:
            iteration = 0
            while True:
                iteration += 1
                if iteration % 10 == 0:  # Log every 10 iterations
                    logger.info(f"Polling loop: iteration {iteration}")
                try:
                    # Poll symbols in batches
                    for batch in batch_iter(self._symbols, self.batch_size):
                        await self._poll_batch(batch)

                    # Sleep until next minute boundary
                    await self._sleep_until_next_minute()

                except asyncio.CancelledError:
                    logger.info("Polling loop cancelled (inner)")
                    raise
                except Exception as e:
                    logger.error(f"Polling error: {e}", exc_info=True)
                    await asyncio.sleep(5)  # Retry after short delay

            logger.critical("CRITICAL: Polling loop while True exited - this should never happen!")

        except asyncio.CancelledError:
            logger.info("Polling task cancelled (outer)")
            raise
        except Exception as e:
            logger.critical(f"Polling loop crashed unexpectedly: {e}", exc_info=True)
            raise
        finally:
            logger.info(f"Polling loop exiting after {iteration} iterations")

    async def _sleep_until_next_minute(self) -> None:
        """Sleep until the next minute boundary."""
        now = datetime.now(timezone.utc)
        next_minute = (now + timedelta(minutes=1)).replace(second=0, microsecond=0)
        sleep_seconds = (next_minute - now).total_seconds()

        logger.debug(f"Polling: Sleeping {sleep_seconds:.1f}s until next minute")
        await asyncio.sleep(max(1, sleep_seconds))

    async def _poll_batch(self, symbols: List[str]) -> None:
        """Poll for latest bars for a batch of symbols.

        Args:
            symbols: List of symbols to poll in a single batch request
        """
        try:
            # Get last 2 bars (current + previous) for all symbols in batch
            request = StockBarsRequest(
                symbol_or_symbols=symbols,
                timeframe=TimeFrame.Minute,
                limit=2,
                start=None,  # Get latest
            )

            bars_by_symbol = self.client.get_stock_bars(request)

            # Process each symbol's bars
            # BarSet.data is a dict: {symbol: [bar1, bar2, ...]}
            for symbol, bar_list in bars_by_symbol.data.items():
                await self._process_bar_list(symbol, bar_list)

        except Exception as e:
            logger.warning(f"Batch polling error for {symbols}: {e}")
            # Don't re-raise - let the loop continue with other batches

    async def _process_bar_list(self, symbol: str, bar_list) -> None:
        """Process a list of bars for a single symbol.

        Args:
            symbol: The symbol for the bars
            bar_list: List of bar objects from Alpaca API
        """
        if not bar_list:
            return

        latest_bar = bar_list[-1]  # Most recent

        # Check if this is a new bar (different timestamp)
        last_ts = self._last_bars.get(symbol)
        if last_ts == latest_bar.timestamp:
            return  # Already processed this bar

        # New bar!
        self._last_bars[symbol] = latest_bar.timestamp

        # Invoke handler
        if self.on_bar:
            bar = PollingBar(symbol, latest_bar)
            await self.on_bar(bar)
            logger.debug(f"Polled bar: {symbol} @ {latest_bar.timestamp} ${latest_bar.close}")

    async def _poll_symbol(self, symbol: str) -> None:
        """Poll for latest bar for a single symbol.

        This method is kept for backwards compatibility and testing.
        For batch operations, use _poll_batch().

        Args:
            symbol: The symbol to poll
        """
        await self._poll_batch([symbol])

    async def stop(self) -> None:
        """Stop polling stream."""
        if self._polling_task:
            self._polling_task.cancel()
            try:
                await self._polling_task
            except asyncio.CancelledError:
                pass

        self.market_connected = False
        self.trade_connected = False
        logger.info("Polling stream stopped")

    async def reconnect_market_stream(self, symbols: list[str]) -> bool:
        """Reconnect market stream."""
        logger.info(f"Reconnecting market polling for {len(symbols)} symbols")
        self._symbols = symbols
        return True
