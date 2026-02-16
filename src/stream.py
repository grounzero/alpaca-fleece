"""WebSocket stream - connectivity only (raw passthrough).

This module owns:
- Connecting to Alpaca WebSocket streams
- Reconnect logic with exponential backoff
- Delivering raw SDK objects to DataHandler
- Rate limit protection (HTTP 429 handling)
- Batched subscription (prevent rate limits)

    MUST NOT:
    - Convert raw SDK objects to canonical internal events (handlers do this)
    - Write to SQLite
    - Publish to EventBus
"""

import asyncio
import logging
import time
from typing import Any, Callable, List, Optional

from alpaca.data.enums import DataFeed
from alpaca.data.live import CryptoDataStream, StockDataStream
from alpaca.trading.stream import TradingStream

from src.rate_limiter import RateLimiter
from src.utils import batch_iter

logger = logging.getLogger(__name__)


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
        crypto_symbols: Optional[List[str]] = None,
    ) -> None:
        """Initialise stream.

        Args:
            api_key: Alpaca API key
            secret_key: Alpaca secret key
            paper: True for paper trading
            feed: "iex" (free) or "sip" (paid)
            crypto_symbols: List of crypto symbols (e.g., ['BTC/USD', 'ETH/USD'])
        """
        self.api_key = api_key
        self.secret_key = secret_key
        self.paper = paper
        # Convert string feed to enum
        if isinstance(feed, str):
            self.feed = DataFeed.IEX if feed.lower() == "iex" else DataFeed.SIP
        else:
            self.feed = feed

        # Store crypto symbols list for symbol separation
        self._crypto_symbols_config: List[str] = crypto_symbols or []

        # Streams (lazy init)
        self.stock_stream: Optional[StockDataStream] = None
        self.crypto_stream: Optional[CryptoDataStream] = None
        self.trade_updates_stream: Optional[TradingStream] = None

        # Symbol tracking
        self._equity_symbols: List[str] = []
        self._crypto_symbols: List[str] = []

        # Callbacks (provided by DataHandler)
        self.on_bar: Optional[Callable[..., Any]] = None
        self.on_order_update: Optional[Callable[..., Any]] = None
        self.on_market_disconnect: Optional[Callable[..., Any]] = None
        self.on_trade_disconnect: Optional[Callable[..., Any]] = None

        # State
        self.stock_stream_connected = False
        self.crypto_stream_connected = False
        self.market_connected = False  # True if any market stream is connected
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
        on_bar: Callable[..., Any],
        on_order_update: Callable[..., Any],
        on_market_disconnect: Callable[..., Any],
        on_trade_disconnect: Callable[..., Any],
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
        """Start all streams (stock, crypto, trade updates).

        Args:
            symbols: List of symbols to subscribe to
        """
        try:
            # Separate symbols into equity and crypto based on config.
            # Use exact matches only — do not manipulate symbol strings.
            crypto_set = set(self._crypto_symbols_config)
            self._equity_symbols = [s for s in symbols if s not in crypto_set]
            self._crypto_symbols = [s for s in symbols if s in crypto_set]

            if self._crypto_symbols:
                logger.info(
                    f"WebSocket symbol routing: {len(self._equity_symbols)} equity, "
                    f"{len(self._crypto_symbols)} crypto ({', '.join(self._crypto_symbols)})"
                )

            # Start equity/crypto streams and trade updates. Cleanups now
            # operate on the presence of stream objects instead of
            # auxiliary "started_*" flags to avoid leaking partially
            # created streams when subscription fails.
            try:
                # Start equity stream if we have equity symbols
                if self._equity_symbols:
                    await self._start_stock_stream(self._equity_symbols)

                # Start crypto stream if we have crypto symbols
                if self._crypto_symbols:
                    await self._start_crypto_stream(self._crypto_symbols)

                # Update market_connected flag
                self.market_connected = self.stock_stream_connected or self.crypto_stream_connected

                # Start trade updates stream
                await self._start_trade_stream()
            except Exception:
                # Cleanup any streams that started successfully to avoid leaks.
                # Also ensure trade stream is closed if partially created by
                # `_start_trade_stream()` to keep internal flags consistent.
                try:
                    # If a stock stream object was created (even if the
                    # `started_stock` flag was not set due to a subsequent
                    # subscription/start failure) close it to avoid leaking
                    # the underlying connection/task.
                    if self.stock_stream is not None:
                        try:
                            await self.stock_stream.close()
                        except Exception:
                            logger.exception("Failed closing stock stream during startup cleanup")
                        finally:
                            self.stock_stream = None
                            self.stock_stream_connected = False

                    # Same for the crypto stream: close if an object exists
                    # regardless of the `started_crypto` flag state.
                    if self.crypto_stream is not None:
                        try:
                            await self.crypto_stream.close()
                        except Exception:
                            logger.exception("Failed closing crypto stream during startup cleanup")
                        finally:
                            self.crypto_stream = None
                            self.crypto_stream_connected = False

                    # If a trade updates stream was created before the failure,
                    # close it as well and reset the connected flag. Use a local
                    # variable so the type-checker recognises the value is not None
                    # when calling `close()`.
                    tstream = self.trade_updates_stream
                    if tstream is not None:
                        try:
                            await tstream.close()
                        except Exception:
                            logger.exception(
                                "Failed closing trade updates stream during startup cleanup"
                            )
                        finally:
                            self.trade_updates_stream = None
                            self.trade_connected = False

                    # Reset market_connected flag after cleanup
                    self.market_connected = False
                except Exception:
                    logger.exception("Unexpected error during startup cleanup")

                # Re-raise to outer handler which converts to StreamError
                raise
        except Exception as e:
            raise StreamError(f"Failed to start streams: {e}") from e

    async def _start_stock_stream(
        self, symbols: list[str], batch_size: int = 10, batch_delay: float = 1.0
    ) -> None:
        """Start stock market data stream with batched subscriptions.

        Args:
            symbols: List of equity symbols to subscribe to
            batch_size: Number of symbols per batch (default: 10)
            batch_delay: Delay in seconds between batches (default: 1.0)
        """
        self.stock_stream = StockDataStream(
            api_key=self.api_key,
            secret_key=self.secret_key,
            feed=self.feed,
        )

        # Register bar handler (raw passthrough)
        async def handle_bar(bar: Any) -> None:
            if self.on_bar:
                await self.on_bar(bar)

        # Subscribe in batches to avoid rate limits
        logger.info(f"Subscribing to {len(symbols)} equity symbols in batches of {batch_size}")
        num_batches = (len(symbols) + batch_size - 1) // batch_size
        subscription_failed = False
        for i, batch in enumerate(batch_iter(symbols, batch_size)):
            logger.info(f"Subscribing equity batch {i + 1}/{num_batches} size={len(batch)}")
            try:
                self.stock_stream.subscribe_bars(handle_bar, *batch)
            except Exception as e:
                subscription_failed = True
                logger.exception("Failed subscribing equity batch %s: %s", batch, e)

            # Delay between batches (except after last batch)
            if i < num_batches - 1:
                await asyncio.sleep(batch_delay)

        if subscription_failed:
            logger.error(
                "One or more equity subscription batches failed; not starting stock stream"
            )
            raise StreamError("Failed to subscribe to one or more equity symbol batches")

        # Start stream using native async _run_forever() instead of sync run()
        # This avoids the asyncio.run() conflict
        asyncio.create_task(self.stock_stream._run_forever())
        self.stock_stream_connected = True
        logger.info(
            f"Stock stream connected: {len(symbols)} equity symbols in {num_batches} batch(es)"
        )

    async def _start_crypto_stream(
        self, symbols: list[str], batch_size: int = 10, batch_delay: float = 1.0
    ) -> None:
        """Start crypto market data stream with batched subscriptions.

        Args:
            symbols: List of crypto symbols to subscribe to (e.g., ['BTC/USD', 'ETH/USD'])
            batch_size: Number of symbols per batch (default: 10)
            batch_delay: Delay in seconds between batches (default: 1.0)
        """
        self.crypto_stream = CryptoDataStream(
            api_key=self.api_key,
            secret_key=self.secret_key,
        )

        # Register bar handler (raw passthrough)
        async def handle_bar(bar: Any) -> None:
            if self.on_bar:
                await self.on_bar(bar)

        # Subscribe in batches to avoid rate limits
        logger.info(f"Subscribing to {len(symbols)} crypto symbols in batches of {batch_size}")
        num_batches = (len(symbols) + batch_size - 1) // batch_size
        subscription_failed = False
        for i, batch in enumerate(batch_iter(symbols, batch_size)):
            logger.info(f"Subscribing crypto batch {i + 1}/{num_batches} size={len(batch)}")
            try:
                self.crypto_stream.subscribe_bars(handle_bar, *batch)
            except Exception as e:
                subscription_failed = True
                logger.exception("Failed subscribing crypto batch %s: %s", batch, e)

            # Delay between batches (except after last batch)
            if i < num_batches - 1:
                await asyncio.sleep(batch_delay)

        if subscription_failed:
            logger.error(
                "One or more crypto subscription batches failed; not starting crypto stream"
            )
            try:
                if getattr(self, "crypto_stream", None) is not None:
                    close_method = getattr(self.crypto_stream, "close", None)
                    if callable(close_method):
                        close_method()
            except Exception as close_exc:
                logger.warning(
                    "Error while closing crypto stream after failed subscription: %s",
                    close_exc,
                )
            finally:
                # Ensure internal state reflects that the crypto stream is not connected
                self.crypto_stream = None
                self.crypto_stream_connected = False
            raise StreamError("Failed to subscribe to one or more crypto symbol batches")
        # Start stream using native async _run_forever() instead of sync run()
        asyncio.create_task(self.crypto_stream._run_forever())
        self.crypto_stream_connected = True
        logger.info(
            f"Crypto stream connected: {len(symbols)} crypto symbols in {num_batches} batch(es)"
        )

    async def _start_trade_stream(self) -> None:
        """Start trade updates stream."""
        self.trade_updates_stream = TradingStream(
            api_key=self.api_key,
            secret_key=self.secret_key,
        )

        # Register order update handler (raw passthrough)
        async def handle_trade_update(update: Any) -> None:
            if self.on_order_update:
                await self.on_order_update(update)

        self.trade_updates_stream.subscribe_trade_updates(handle_trade_update)

        # Start stream using native async _run_forever() instead of sync run()
        asyncio.create_task(self.trade_updates_stream._run_forever())  # type: ignore[no-untyped-call]
        self.trade_connected = True
        logger.info("Trade stream connected")

    async def stop(self) -> None:
        """Stop all streams (stock, crypto, trade updates)."""
        if self.stock_stream:
            await self.stock_stream.close()
            self.stock_stream_connected = False

        if self.crypto_stream:
            await self.crypto_stream.close()
            self.crypto_stream_connected = False

        self.market_connected = False

        if self.trade_updates_stream:
            await self.trade_updates_stream.close()
            self.trade_connected = False

        logger.info("All streams stopped")

    async def reconnect_market_stream(self, symbols: list[str]) -> bool:
        """Attempt to reconnect market streams (stock and crypto) with rate limit protection.

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
            # Clamp remaining to avoid passing negative values to asyncio.sleep
            sleep_for = max(0.0, remaining)
            if sleep_for <= 0.0:
                logger.debug("Market stream: backoff already elapsed; proceeding to retry")
            else:
                logger.warning(
                    f"Market stream: Rate limited, waiting {sleep_for:.1f}s before retry"
                )
                await asyncio.sleep(sleep_for)

        try:
            # Close existing streams first to prevent connection/task leaks
            if self.stock_stream:
                logger.debug("Closing existing stock stream before reconnect")
                await self.stock_stream.close()
                self.stock_stream = None
                self.stock_stream_connected = False

            if self.crypto_stream:
                logger.debug("Closing existing crypto stream before reconnect")
                await self.crypto_stream.close()
                self.crypto_stream = None
                self.crypto_stream_connected = False

            # Reset market_connected flag
            self.market_connected = False

            # Separate symbols into equity and crypto
            crypto_set = set(self._crypto_symbols_config)
            equity_symbols = [s for s in symbols if s not in crypto_set]
            crypto_symbols_list = [s for s in symbols if s in crypto_set]

            # Reconnect equity and crypto streams. If one starts successfully
            # but a subsequent start fails, close the successfully started
            # stream to avoid leaving the system in a partially connected state.
            # Cleanup will be based on the presence of stream objects rather
            # than auxiliary flags to avoid leaking partially-created streams.

            try:
                # Start equity stream if needed
                if equity_symbols:
                    await self._start_stock_stream(equity_symbols)

                # Start crypto stream if needed
                if crypto_symbols_list:
                    await self._start_crypto_stream(crypto_symbols_list)

                # Update market_connected flag only after both attempts
                self.market_connected = self.stock_stream_connected or self.crypto_stream_connected

                self.market_rate_limiter.record_success()
                logger.info("Market streams reconnected (rate limiter reset)")
                return True
            except ValueError as e:
                # Special-case ValueError for rate-limit/subscription errors
                msg = str(e).lower()
                if "429" in msg or "connection limit" in msg or "subscription" in msg:
                    logger.warning("Market stream: HTTP 429 / subscription rate limit hit")
                else:
                    logger.error(f"Market stream reconnect failed: {e}")

                self.market_rate_limiter.record_failure()

                # Cleanup any streams that started successfully
                # Close any partially-started stock stream instance
                # (the stream object may exist even if `started_stock` is
                # still False due to an exception during subscription).
                if self.stock_stream is not None:
                    try:
                        await self.stock_stream.close()
                    except Exception:
                        logger.exception("Failed closing partially-started stock stream")
                    finally:
                        self.stock_stream = None
                        self.stock_stream_connected = False

                # Close any partially-started crypto stream instance
                if self.crypto_stream is not None:
                    try:
                        await self.crypto_stream.close()
                    except Exception:
                        logger.exception("Failed closing partially-started crypto stream")
                    finally:
                        self.crypto_stream = None
                        self.crypto_stream_connected = False

                self.market_connected = False
                return False
            except Exception as e:
                # Generic failure starting one of the streams - log and cleanup
                logger.error(f"Market stream reconnect failed: {e}")
                self.market_rate_limiter.record_failure()

                # Cleanup any streams that may have been created before the
                # failure. The stream objects may exist even if the
                # `started_*` flags were not set, so base cleanup on the
                # presence of the attributes rather than the flags.
                if self.stock_stream is not None:
                    try:
                        await self.stock_stream.close()
                    except Exception:
                        logger.exception("Failed closing partially-started stock stream")
                    finally:
                        self.stock_stream = None
                        self.stock_stream_connected = False

                if self.crypto_stream is not None:
                    try:
                        await self.crypto_stream.close()
                    except Exception:
                        logger.exception("Failed closing partially-started crypto stream")
                    finally:
                        self.crypto_stream = None
                        self.crypto_stream_connected = False

                self.market_connected = False
                return False
        except ValueError as e:
            # Outer ValueError (e.g., from close/start operations)
            if "429" in str(e) or "connection limit" in str(e).lower():
                logger.warning("Market stream: HTTP 429 rate limit hit")
            else:
                logger.error(f"Market stream reconnect failed: {e}")

            self.market_rate_limiter.record_failure()
            return False
        except Exception as e:
            logger.error(f"Market stream reconnect failed: {e}")
            self.market_rate_limiter.record_failure()
            return False
