"""HTTP polling stream - fallback when WebSocket connection limit exceeded.

This module provides the same interface as Stream but uses HTTP polling instead of WebSocket.
"""

import asyncio
import logging
import sqlite3
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Dict, List, Optional

from alpaca.data.enums import DataFeed
from alpaca.data.historical import StockHistoricalDataClient
from alpaca.data.requests import StockBarsRequest
from alpaca.data.timeframe import TimeFrame
from alpaca.trading.client import TradingClient

from src.utils import batch_iter

logger = logging.getLogger(__name__)

# Module-level constants for Alpaca API URLs
PAPER_API_URL = "https://paper-api.alpaca.markets"
LIVE_API_URL = "https://api.alpaca.markets"


# Module-level order update wrappers to avoid redefining classes on every poll
class OrderUpdateWrapper:
    """Wrapper for order update events."""

    def __init__(self, order_data: Any):
        self.order = OrderWrapper(order_data)
        self.at = datetime.now(timezone.utc)


class OrderWrapper:
    """Wrapper for order data from Alpaca API."""

    def __init__(self, data: Any):
        if isinstance(data, dict):
            self.id = data.get("id")
            self.client_order_id = data.get("client_order_id")
            self.symbol = data.get("symbol")
            status_val = data.get("status", "unknown")
            self.status = StatusWrapper(status_val)
            self.filled_qty = data.get("filled_qty")
            self.filled_avg_price = data.get("filled_avg_price")
        else:
            # Order object
            self.id = getattr(data, "id", None)
            self.client_order_id = getattr(data, "client_order_id", None)
            self.symbol = getattr(data, "symbol", None)
            status_val = getattr(data, "status", None)
            # Handle enum status values properly
            if status_val:
                if hasattr(status_val, "value"):
                    status_str = str(getattr(status_val, "value"))
                else:
                    status_str = str(status_val)
            else:
                status_str = "unknown"
            self.status = StatusWrapper(status_str)
            self.filled_qty = getattr(data, "filled_qty", None)
            self.filled_avg_price = getattr(data, "filled_avg_price", None)


class StatusWrapper:
    """Wrapper for order status."""

    def __init__(self, value: str):
        self.value = value


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
        self.feed: str = feed or "iex"  # Keep as string for API compatibility
        self._data_feed: DataFeed = DataFeed(self.feed)  # Internal enum
        self.batch_size = batch_size

        # Historical data client for polling
        self.client = StockHistoricalDataClient(api_key, secret_key)

        # Trading client for order status queries
        paper_url = PAPER_API_URL if paper else LIVE_API_URL
        self.trading_client = TradingClient(
            api_key, secret_key, paper=paper, raw_data=True, url_override=paper_url
        )

        self.market_connected = False
        self.trade_connected = False
        self.on_bar: Optional[Callable[[Any], Any]] = None
        self.on_order_update: Optional[Callable[[Any], Any]] = None
        self.on_market_disconnect: Optional[Callable[[], Any]] = None
        self.on_trade_disconnect: Optional[Callable[[], Any]] = None

        self._last_bars: dict[str, datetime] = {}  # Track last known bar timestamp per symbol
        self._polling_task: Optional[asyncio.Task[None]] = None
        self._order_polling_task: Optional[asyncio.Task[None]] = None
        self._symbols: list[str] = []
        self._use_fallback: bool = False  # Set to True if SIP feed requires subscription
        self._fallback_logged: bool = False  # Track if we already logged fallback message
        self._symbols_with_data: set[str] = set()  # Track which symbols have received data
        self._poll_iterations: int = 0  # Track polling iterations for periodic reporting
        self._db_path: str = "data/trades.db"  # Default DB path

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
        if self._use_fallback or self._data_feed == DataFeed.IEX:
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

        # Clear session state
        self._symbols_with_data.clear()
        self._last_bars.clear()

        # Validate feed subscription on startup (only for SIP)
        await self._validate_feed()

        effective_batch = self._effective_batch_size
        num_batches = (len(symbols) + effective_batch - 1) // effective_batch

        if effective_batch != self.batch_size:
            active_feed = "iex" if self._use_fallback else self.feed
            logger.info(
                f"Polling stream started for {len(symbols)} symbols "
                f"in {num_batches} batch(es) of {effective_batch} "
                f"(configured: {self.batch_size}, reduced for {active_feed} feed) "
                f"(1-min polling)"
            )
        else:
            logger.info(
                f"Polling stream started for {len(symbols)} symbols "
                f"in {num_batches} batch(es) of {effective_batch} (1-min polling)"
            )

        # Start polling tasks
        self._polling_task = asyncio.create_task(self._poll_loop())
        self._order_polling_task = asyncio.create_task(self._poll_order_updates())

    async def _validate_feed(self) -> None:
        """Validate feed subscription on startup.

        For SIP feed: tests with a single AAPL bar request.
        If SIP fails with subscription error, falls back to IEX.
        For IEX feed: no validation needed.

        Raises:
            Exception: If validation fails for non-subscription reasons.
        """
        if self._data_feed == DataFeed.IEX:
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
                feed=self._data_feed,
            )
            await asyncio.to_thread(self.client.get_stock_bars, request)
            # Test passed - SIP is available
            logger.info(f"Using {self._data_feed.value.upper()} feed")
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
                        missing = len(self._symbols) - len(self._symbols_with_data)
                        logger.debug(
                            f"Symbol coverage: {len(self._symbols_with_data)}/{len(self._symbols)} (missing: {missing})"
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

    async def _poll_order_updates(self) -> None:
        """Poll for order status changes every 2 seconds.

        Checks all orders with non-terminal status in SQLite, queries Alpaca for current status,
        and triggers on_order_update when any status change is detected.
        """
        logger.info("Order update polling started")
        try:
            while True:
                try:
                    await self._check_order_status()
                except asyncio.CancelledError:
                    raise
                except Exception as e:
                    logger.error(f"Order polling error: {e}", exc_info=True)

                await asyncio.sleep(2)

        except asyncio.CancelledError:
            logger.info("Order polling cancelled")
            raise
        except Exception as e:
            logger.critical(f"Order polling crashed: {e}", exc_info=True)
            raise

    def _hex_to_uuid(self, hex_str: str) -> str:
        """Convert hex string to UUID format.

        Alpaca API expects UUID format (51057fad-52fa-6ca2-...)
        but order IDs may be stored as hex strings (51057fad52fa6ca2).

        Args:
            hex_str: The order ID string, either hex or UUID format

        Returns:
            UUID formatted string
        """
        if len(hex_str) == 32:
            return f"{hex_str[:8]}-{hex_str[8:12]}-{hex_str[12:16]}-{hex_str[16:20]}-{hex_str[20:]}"
        return hex_str  # Already in UUID format

    async def _check_order_status(self) -> None:
        """Check status of submitted orders and trigger updates."""
        # Get submitted orders from SQLite in a worker thread to avoid blocking
        submitted_orders = await asyncio.to_thread(self._get_submitted_orders)

        for order in submitted_orders:
            try:
                # Convert hex order ID to UUID format if needed
                alpaca_order_id = self._hex_to_uuid(order["alpaca_order_id"])

                # Query Alpaca for current status in a worker thread
                alpaca_order = await asyncio.to_thread(
                    self.trading_client.get_order_by_id,
                    alpaca_order_id,
                )

                if not alpaca_order:
                    logger.warning(f"Order {order['client_order_id']} not found in Alpaca")
                    continue

                # Handle both dict and Order object returns
                if isinstance(alpaca_order, dict):
                    current_status = alpaca_order.get("status", "unknown")
                    filled_qty = alpaca_order.get("filled_qty")
                    filled_avg_price = alpaca_order.get("filled_avg_price")
                else:
                    # Normalize enum status values properly (use .value when present)
                    status_val = getattr(alpaca_order, "status", None)
                    if status_val:
                        if hasattr(status_val, "value"):
                            current_status = str(getattr(status_val, "value")).lower()
                        else:
                            current_status = str(status_val).lower()
                    else:
                        current_status = "unknown"
                    filled_qty = getattr(alpaca_order, "filled_qty", None)
                    filled_avg_price = getattr(alpaca_order, "filled_avg_price", None)

                # Coerce numeric values to float for SQLite compatibility
                if filled_qty is not None:
                    filled_qty = float(filled_qty)
                if filled_avg_price is not None:
                    filled_avg_price = float(filled_avg_price)

                # If status changed, persist to DB and trigger update
                if current_status != order["status"]:
                    logger.info(
                        f"Order {order['client_order_id']} status: "
                        f"{order['status']} -> {current_status}"
                    )

                    # Persist status change to SQLite to prevent duplicate updates
                    await asyncio.to_thread(
                        self._update_order_status,
                        order["client_order_id"],
                        current_status,
                        filled_qty,
                        filled_avg_price,
                    )

                    # Create normalized update event
                    update_event = self._create_order_update_event(alpaca_order)

                    # Call handler if registered
                    if self.on_order_update:
                        await self.on_order_update(update_event)

            except Exception as e:
                logger.error(
                    f"Failed to check order {order.get('client_order_id', 'unknown')}: {e}"
                )

    def _get_submitted_orders(self) -> List[Dict[str, Any]]:
        """Get all submitted orders from SQLite that need status checking."""
        try:
            # Use context manager to ensure connection is closed even on errors
            with sqlite3.connect(self._db_path) as conn:
                cursor = conn.cursor()

                # Get orders with non-terminal status and valid alpaca_order_id
                cursor.execute("""
                    SELECT client_order_id, symbol, side, qty, status,
                           filled_qty, alpaca_order_id
                    FROM order_intents
                    WHERE status IN ('submitted', 'pending', 'accepted', 'new', 'partially_filled')
                    AND alpaca_order_id IS NOT NULL
                    AND alpaca_order_id != ''
                    """)

                rows = cursor.fetchall()

                orders = []
                for row in rows:
                    orders.append(
                        {
                            "client_order_id": row[0],
                            "symbol": row[1],
                            "side": row[2],
                            "qty": row[3],
                            "status": row[4],
                            "filled_qty": row[5] or 0,
                            "alpaca_order_id": row[6],
                        }
                    )

                return orders

        except sqlite3.Error as e:
            logger.error(f"Database error fetching orders: {e}")
            return []

    def _update_order_status(
        self,
        client_order_id: str,
        status: str,
        filled_qty: Optional[Any],
        filled_avg_price: Optional[Any],
    ) -> None:
        """Update order status in SQLite to prevent duplicate updates.

        Args:
            client_order_id: The client order ID
            status: New status from Alpaca
            filled_qty: Filled quantity (optional)
            filled_avg_price: Filled average price (optional)
        """
        try:
            with sqlite3.connect(self._db_path) as conn:
                cursor = conn.cursor()
                cursor.execute(
                    """
                    UPDATE order_intents
                    SET status = ?, filled_qty = ?, filled_avg_price = ?, updated_at_utc = ?
                    WHERE client_order_id = ?
                    """,
                    (
                        status,
                        filled_qty,
                        filled_avg_price,
                        datetime.now(timezone.utc).isoformat(),
                        client_order_id,
                    ),
                )
                conn.commit()
        except sqlite3.Error as e:
            logger.error(f"Database error updating order {client_order_id}: {e}")

    def _create_order_update_event(self, alpaca_order: Any) -> Any:
        """Create a normalized order update event from Alpaca order data.

        Args:
            alpaca_order: Order data from Alpaca API (dict or Order object)

        Returns:
            Normalized order update event matching expected interface
        """
        # Use module-level wrapper classes (defined once, not per-call)
        return OrderUpdateWrapper(alpaca_order)

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
                active_feed = self._data_feed

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

            bars_by_symbol = await asyncio.to_thread(self.client.get_stock_bars, request)

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

        if self._order_polling_task:
            self._order_polling_task.cancel()
            try:
                await self._order_polling_task
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
