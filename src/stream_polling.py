"""HTTP polling stream - fallback when WebSocket connection limit exceeded.

This module provides the same interface as Stream but uses HTTP polling instead of WebSocket.
"""

import asyncio
import logging
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, List, Optional

from alpaca.data.enums import DataFeed
from alpaca.data.historical import StockHistoricalDataClient
from alpaca.data.requests import StockBarsRequest
from alpaca.data.timeframe import TimeFrame

from src.utils import batch_iter

logger = logging.getLogger(__name__)


class PollingBar:
    """Minimal bar object matching alpaca Bar interface."""

    def __init__(self, symbol: str, bar: Any) -> None:
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
        self.feed: DataFeed = DataFeed(feed) if feed else DataFeed.IEX
        self.batch_size = batch_size

        # Historical data client for polling
        self.client = StockHistoricalDataClient(api_key, secret_key)

        self.market_connected = False
        self.trade_connected = False
        self.on_bar: Optional[Callable[[Any], Any]] = None
        self.on_order_update: Optional[Callable[[Any], Any]] = None
        self.on_market_disconnect: Optional[Callable[[], Any]] = None
        self.on_trade_disconnect: Optional[Callable[[], Any]] = None

        self._last_bars: dict[str, datetime] = {}  # Track last known bar timestamp per symbol
        self._polling_task: Optional[asyncio.Task[None]] = None
        self._symbols: list[str] = []
        self._use_fallback: bool = False  # Set to True if SIP feed requires subscription
        self._fallback_logged: bool = False  # Track if we already logged fallback message
        self._symbols_with_data: set[str] = set()  # Track which symbols have received data
        self._poll_iterations: int = 0  # Track polling iterations for periodic reporting

    def register_handlers(
        self,
        on_bar: Optional[Callable[[Any], Any]] = None,
        on_order_update: Optional[Callable[[Any], Any]] = None,
        on_market_disconnect: Optional[Callable[[], Any]] = None,
        on_trade_disconnect: Optional[Callable[[], Any]] = None,
    ) -> None:
        """Register event handlers."""
        self.on_bar = on_bar
        self.on_order_update = on_order_update
        self.on_market_disconnect = on_market_disconnect
        self.on_trade_disconnect = on_trade_disconnect

    @property
    def _effective_batch_size(self) -> int:
        """Return effective batch size based on feed type.

        IEX feed has a bug where batch requests with 3+ symbols only return
        data for 2 symbols. SIP feed works correctly with larger batches.

        Returns:
            2 for IEX feed (including SIP fallback to IEX)
            Configured batch_size for SIP feed
        """
        # If using IEX (either directly or as fallback), limit to 2 symbols
        # to work around the Alpaca API bug
        if self._use_fallback or self.feed == DataFeed.IEX:
            return 2
        # SIP feed supports full batch size
        return self.batch_size

    async def start(self, symbols: list[str]) -> None:
        """Start polling stream.

        Args:
            symbols: List of symbols to poll
        """
        self._symbols = symbols
        self.market_connected = True
        self.trade_connected = True

        # Reset fallback log flag for each new polling session
        self._fallback_logged = False

        # Validate feed subscription on startup (only for SIP)
        await self._validate_feed()

        effective_batch = self._effective_batch_size
        num_batches = (len(symbols) + effective_batch - 1) // effective_batch

        if effective_batch != self.batch_size:
            logger.info(
                f"Polling stream started for {len(symbols)} symbols "
                f"in {num_batches} batch(es) of {effective_batch} "
                f"(configured: {self.batch_size}, reduced for {self.feed.value} feed) "
                f"(1-min polling)"
            )
        else:
            logger.info(
                f"Polling stream started for {len(symbols)} symbols "
                f"in {num_batches} batch(es) of {effective_batch} (1-min polling)"
            )

        # Start polling task
        self._polling_task = asyncio.create_task(self._poll_loop())

    async def _validate_feed(self) -> None:
        """Validate feed subscription on startup.

        For SIP feed: tests with a single AAPL bar request.
        If SIP fails with subscription error, falls back to IEX.
        For IEX feed: no validation needed.

        Raises:
            Exception: If validation fails for non-subscription reasons.
        """
        if self.feed == DataFeed.IEX:
            logger.info("Using IEX feed")
            self._use_fallback = False
            return

        # SIP feed - test subscription with a single AAPL bar request
        test_symbol = "AAPL"
        start_time = datetime.now(timezone.utc) - timedelta(minutes=5)

        try:
            request = StockBarsRequest(
                symbol_or_symbols=test_symbol,
                timeframe=TimeFrame.Minute,
                start=start_time,
                limit=1,
                feed=self.feed,
            )
            self.client.get_stock_bars(request)
            # Test passed - SIP is available
            logger.info(f"Using {self.feed.value.upper()} feed")
            self._use_fallback = False
        except Exception as e:
            error_message = str(e).lower()
            # Check for subscription-related errors
            if "subscription" in error_message and "permit" in error_message:
                logger.warning(
                    "SIP feed requires subscription; falling back to IEX. "
                    "To suppress this warning, set stream_feed: iex in config/trading.yaml"
                )
                self._use_fallback = True
            else:
                # Non-subscription error - re-raise to not mask real problems
                raise

    async def _poll_loop(self) -> None:
        """Poll for new bars every minute."""
        logger.info("Polling loop started")
        try:
            iteration = 0
            while True:
                iteration += 1
                self._poll_iterations = iteration
                if iteration % 10 == 0:  # Log every 10 iterations
                    logger.info(f"Polling loop: iteration {iteration}")
                try:
                    # Poll symbols in batches (use effective batch size based on feed)
                    effective_batch = self._effective_batch_size
                    for batch in batch_iter(self._symbols, effective_batch):
                        await self._poll_batch(batch)

                    # Periodic report on symbol coverage (every 5 iterations)
                    if iteration % 5 == 0 and self._symbols:
                        missing_symbols = set(self._symbols) - self._symbols_with_data
                        if missing_symbols:
                            logger.warning(
                                f"SYMBOL COVERAGE REPORT: {len(self._symbols_with_data)}/{len(self._symbols)} "
                                f"symbols have received data. Missing: {sorted(missing_symbols)}"
                            )
                        else:
                            logger.info(
                                f"SYMBOL COVERAGE REPORT: All {len(self._symbols)} symbols have received data"
                            )

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
            # Determine which feed to use
            if self._use_fallback:
                active_feed: DataFeed = DataFeed.IEX
                if not self._fallback_logged:
                    logger.info("Using IEX fallback feed (SIP unavailable)")
                    self._fallback_logged = True
            else:
                active_feed = self.feed

            # Get bars from last 5 minutes to ensure fresh data
            # start=None returns stale cached data, so we use explicit time window
            start_time = datetime.now(timezone.utc) - timedelta(minutes=5)

            logger.debug(f"Polling batch of {len(symbols)} symbols: {symbols}")

            request = StockBarsRequest(
                symbol_or_symbols=symbols,
                timeframe=TimeFrame.Minute,
                start=start_time,
                limit=10,  # Get more bars to ensure we have latest
                feed=active_feed,
            )

            bars_by_symbol = self.client.get_stock_bars(request)

            # Process each symbol's bars
            # BarSet.data is a dict: {symbol: [bar1, bar2, ...]}
            bars_data = getattr(bars_by_symbol, "data", {})

            # Log which symbols returned data vs which didn't
            returned_symbols = set(bars_data.keys())
            requested_symbols = set(symbols)
            missing_symbols = requested_symbols - returned_symbols

            if missing_symbols:
                logger.debug(f"No bar data returned for symbols: {sorted(missing_symbols)}")
            logger.debug(
                f"Received bar data for {len(returned_symbols)}/{len(symbols)} symbols: {sorted(returned_symbols)}"
            )

            for symbol, bar_list in bars_data.items():
                await self._process_bar_list(symbol, bar_list)

        except Exception as e:
            logger.warning(f"Batch polling error for {symbols}: {e}")
            # Don't re-raise - let the loop continue with other batches

    async def _process_bar_list(self, symbol: str, bar_list: list[Any]) -> None:
        """Process a list of bars for a single symbol.

        Args:
            symbol: The symbol for the bars
            bar_list: List of bar objects from Alpaca API
        """
        if not bar_list:
            logger.debug(f"No bars returned for {symbol}")
            return

        latest_bar = bar_list[-1]  # Most recent
        logger.debug(
            f"Processing {symbol}: {len(bar_list)} bars, latest={latest_bar.timestamp}, close=${latest_bar.close}"
        )

        # Check if this is a new bar (different timestamp)
        last_ts = self._last_bars.get(symbol)
        if last_ts == latest_bar.timestamp:
            logger.debug(f"Skipping {symbol}: already processed bar at {latest_bar.timestamp}")
            return  # Already processed this bar

        # New bar!
        logger.debug(f"New bar for {symbol}: last_ts={last_ts}, new_ts={latest_bar.timestamp}")
        self._last_bars[symbol] = latest_bar.timestamp
        self._symbols_with_data.add(symbol)  # Track that this symbol has received data

        # Invoke handler
        if self.on_bar:
            bar = PollingBar(symbol, latest_bar)
            logger.debug(f"Calling on_bar handler for {symbol}")
            await self.on_bar(bar)
            logger.debug(f"Processed bar {symbol} @ {latest_bar.timestamp} ${latest_bar.close}")
        else:
            logger.debug(f"No on_bar handler registered for {symbol}")

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
