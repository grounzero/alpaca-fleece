"""HTTP polling stream - fallback when WebSocket connection limit exceeded.

This module provides the same interface as Stream but uses HTTP polling instead of WebSocket.
"""

import asyncio
import logging
import os
import sqlite3
import uuid
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Dict, List, Optional

from alpaca.data.enums import DataFeed
from alpaca.data.historical import StockHistoricalDataClient
from alpaca.data.requests import StockBarsRequest
from alpaca.data.timeframe import TimeFrame
from alpaca.trading.client import TradingClient

from src.state_store import StateStore
from src.utils import batch_iter

logger = logging.getLogger(__name__)

# Module-level Alpaca API URLs. Allow overriding via environment variables so
# deployments can change endpoints without modifying code.
PAPER_API_URL = os.getenv("ALPACA_PAPER_API_URL", "https://paper-api.alpaca.markets")
LIVE_API_URL = os.getenv("ALPACA_LIVE_API_URL", "https://api.alpaca.markets")


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
            # Handle side attribute with enum support
            side_val = data.get("side")
            if side_val:
                if hasattr(side_val, "value"):
                    side_str = str(getattr(side_val, "value")).lower()
                else:
                    side_str = str(side_val).lower()
            else:
                side_str = "unknown"
            self.side = SideWrapper(side_str)
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
            # Handle side attribute with enum support
            side_val = getattr(data, "side", None)
            if side_val:
                if hasattr(side_val, "value"):
                    side_str = str(getattr(side_val, "value")).lower()
                else:
                    side_str = str(side_val).lower()
            else:
                side_str = "unknown"
            self.side = SideWrapper(side_str)


class StatusWrapper:
    """Wrapper for order status."""

    def __init__(self, value: str):
        # Set status to a lowercase string so all event sources
        # (SDK events, polling, etc.) present consistent status values.
        self.value = str(value).lower()


class SideWrapper:
    """Wrapper for order side (buy/sell).

    Ensures the `.value` attribute is always a lowercase string. Accepts
    enum-like objects (with a `.value`) or plain strings.
    """

    def __init__(self, value: Any):
        # Prefer .value when present (enum-like), else use the value itself
        raw = getattr(value, "value", value)
        if raw is None:
            self.value = "unknown"
        else:
            try:
                self.value = str(raw).lower()
            except Exception:
                self.value = "unknown"


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
        order_polling_concurrency: int = 10,
        db_path: Optional[str] = None,
    ) -> None:
        """Initialise polling stream.

        Args:
            api_key: Alpaca API key
            secret_key: Alpaca secret key
            paper: True for paper trading
            feed: "iex" (free) or "sip" (paid)
            batch_size: Number of symbols per batch request (default: 25)
            order_polling_concurrency: Maximum concurrent Alpaca order requests during polling
                (default: 10)
        """
        self.api_key = api_key
        self.secret_key = secret_key
        self.paper = paper
        # Format feed string for common variants (case/whitespace) and validate
        self.feed: str = (feed or "iex").strip().lower()
        try:
            self._data_feed: DataFeed = DataFeed(self.feed)  # Internal enum
        except Exception as exc:
            raise ValueError(f"Invalid data feed '{self.feed}'. Expected 'iex' or 'sip'.") from exc
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
        default_db = os.getenv("DATABASE_PATH", "data/trades.db")
        self._db_path: str = db_path or default_db
        # Maximum concurrent Alpaca order requests during polling
        self._order_polling_concurrency: int = order_polling_concurrency
        # State store for persistence (used by polling path).
        # Initialize lazily so tests can override `self._db_path`.
        self._state_store: Optional[StateStore] = None

    def _get_state_store(self) -> StateStore:
        """Return a StateStore instance for the current DB path, recreating
        it if `self._db_path` has changed (tests modify `_db_path`)."""
        if (
            self._state_store is None
            or getattr(self._state_store, "db_path", None) != self._db_path
        ):
            self._state_store = StateStore(self._db_path)
        return self._state_store

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
            interval = 2.0
            while True:
                try:
                    start = datetime.now(timezone.utc)
                    await self._check_order_status()
                except asyncio.CancelledError:
                    raise
                except Exception as e:
                    logger.error(f"Order polling error: {e}", exc_info=True)

                # Maintain a roughly-fixed cadence: sleep remaining time of interval
                elapsed = (datetime.now(timezone.utc) - start).total_seconds()
                to_sleep = max(0.0, interval - elapsed)
                await asyncio.sleep(to_sleep)

        except asyncio.CancelledError:
            logger.info("Order polling cancelled")
            raise
        except Exception as e:
            logger.critical(f"Order polling crashed: {e}", exc_info=True)
            raise

    def _hex_to_uuid(self, hex_str: Optional[str]) -> Optional[str]:
        """Convert hex string to UUID format.

        Alpaca API expects UUID format (51057fad-52fa-6ca2-...)
        but order IDs may be stored as hex strings (51057fad52fa6ca2).

        Args:
            hex_str: The order ID string, either hex or UUID format

        Returns:
            UUID formatted string or None if input is None
        """
        # Use the stdlib uuid module for parsing/formatting. Accept both
        # hyphenated UUIDs and 32-char hex strings. On parse errors, fall
        # back to returning the original value to avoid breaking lookups.
        try:
            if hex_str is None:
                return hex_str
            # Remove hyphens (if present) and whitespace before parsing
            cleaned = str(hex_str).replace("-", "").strip()
            if len(cleaned) != 32:
                return hex_str
            return str(uuid.UUID(hex=cleaned))
        except (ValueError, AttributeError, TypeError):
            return hex_str

    async def _check_order_status(self) -> None:
        """Check status of submitted orders and trigger updates."""
        # Get submitted orders from SQLite in a worker thread to avoid blocking
        submitted_orders = await asyncio.to_thread(self._get_submitted_orders)
        if not submitted_orders:
            return

        sem = asyncio.Semaphore(self._order_polling_concurrency)

        async def _process_one(order: dict[str, Any]) -> None:
            """Fetch Alpaca order and process status for a single order."""
            client_id = order.get("client_order_id", "unknown")
            try:
                async with sem:
                    # Convert hex order ID to UUID format if needed
                    alpaca_order_id = self._hex_to_uuid(order["alpaca_order_id"])

                    # Query Alpaca for current status in a worker thread
                    # Ensure we pass a non-None string to the trading client
                    alpaca_order = await asyncio.to_thread(
                        self.trading_client.get_order_by_id,
                        alpaca_order_id or order["alpaca_order_id"],
                    )

                if not alpaca_order:
                    logger.warning(f"Order {client_id} not found in Alpaca")
                    return

                # Handle both dict and Order object returns
                if isinstance(alpaca_order, dict):
                    raw_status = alpaca_order.get("status", "unknown")
                    current_status = str(raw_status).lower()
                    filled_qty = alpaca_order.get("filled_qty")
                    filled_avg_price = alpaca_order.get("filled_avg_price")
                else:
                    # Ensure enum status values are in canonical form (use .value when present)
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

                # Coerce numeric values to float for SQLite compatibility.
                # Alpaca SDK objects typically expose numeric types, but
                # when using `raw_data=True` or when the client returns
                # dict/JSON responses the numeric values may be strings.
                # Safely parse them here and fall back to `None` on parse
                # errors to avoid accidentally overwriting DB values.
                def _to_float_safe(val: Any) -> Optional[float]:
                    if val is None:
                        return None
                    if isinstance(val, (int, float)):
                        return float(val)
                    try:
                        s = str(val).strip()
                        if s == "":
                            return None
                        return float(s)
                    except Exception:
                        return None

                parsed_filled_qty = _to_float_safe(filled_qty)
                parsed_filled_avg_price = _to_float_safe(filled_avg_price)

                # Detect whether an event should be emitted:
                # 1. Status changed, OR
                # 2. Cumulative filled qty increased (incremental fill detection)
                status_changed = current_status != order["status"]

                # Compare cumulative fill qty to detect incremental fills
                # Use parse_optional_float for type safety (DB may store as string/Decimal)
                from src.utils import parse_optional_float as parse_float

                stored_filled_qty_raw = order.get("filled_qty")
                # Ensure stored_filled_qty is a concrete float (coerce None -> 0.0)
                if stored_filled_qty_raw is None:
                    stored_filled_qty = 0.0
                else:
                    parsed_stored = parse_float(stored_filled_qty_raw)
                    stored_filled_qty = parsed_stored if parsed_stored is not None else 0.0

                polled_filled_qty = parsed_filled_qty if parsed_filled_qty is not None else 0.0
                fill_qty_increased = polled_filled_qty > stored_filled_qty + 1e-9

                should_emit = status_changed or fill_qty_increased

                if should_emit:
                    if status_changed:
                        logger.info(
                            f"Order {client_id} status: {order['status']} -> {current_status}"
                        )
                    if fill_qty_increased:
                        logger.info(
                            f"Order {client_id} incremental fill detected: "
                            f"stored_qty={stored_filled_qty} -> polled_qty={polled_filled_qty}"
                        )

                    # Persist to DB
                    await asyncio.to_thread(
                        self._update_order_status,
                        client_id,
                        current_status,
                        parsed_filled_qty if parsed_filled_qty is not None else None,
                        parsed_filled_avg_price if parsed_filled_avg_price is not None else None,
                        order.get("alpaca_order_id"),
                    )

                    # Create update event, merging DB row fallback values so
                    # missing Alpaca fields (client_order_id, symbol, side)
                    # don't produce events with empty identifiers.
                    fallback = {
                        "client_order_id": order.get("client_order_id", ""),
                        "symbol": order.get("symbol", ""),
                        "side": order.get("side", "unknown"),
                        "id": order.get("alpaca_order_id", None),
                        "status": current_status,
                        "filled_qty": filled_qty,
                        "filled_avg_price": filled_avg_price,
                    }

                    if isinstance(alpaca_order, dict):
                        merged = {**fallback, **alpaca_order}
                    else:
                        # Extract common attrs from SDK Order-like object
                        extracted = {
                            "id": getattr(alpaca_order, "id", None),
                            "client_order_id": getattr(alpaca_order, "client_order_id", None),
                            "symbol": getattr(alpaca_order, "symbol", None),
                            "status": getattr(alpaca_order, "status", None),
                            "filled_qty": getattr(alpaca_order, "filled_qty", None),
                            "filled_avg_price": getattr(alpaca_order, "filled_avg_price", None),
                            "side": getattr(alpaca_order, "side", None),
                        }
                        # Prefer SDK values when present, but coerce status/filled
                        # fields to the canonical values to avoid
                        # embedding enum or MagicMock objects into the dict.
                        merged = {
                            **fallback,
                            **{k: v for k, v in extracted.items() if v is not None},
                        }

                    # Ensure canonical fields overwrite any raw SDK objects
                    merged["status"] = current_status
                    merged["filled_qty"] = filled_qty
                    merged["filled_avg_price"] = filled_avg_price

                    update_event = self._create_order_update_event(merged)

                    # Call handler if registered
                    if self.on_order_update:
                        await self.on_order_update(update_event)

            except Exception as e:
                logger.error(f"Failed to check order {client_id}: {e}", exc_info=True)

        # Run fetches concurrently but bounded by semaphore
        tasks = [_process_one(o) for o in submitted_orders]
        await asyncio.gather(*tasks)

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

                def _to_optional_float(val: Any) -> Optional[float]:
                    if val is None:
                        return None
                    try:
                        return float(val)
                    except Exception:
                        return None

                for row in rows:
                    orders.append(
                        {
                            "client_order_id": row[0],
                            "symbol": row[1],
                            "side": row[2],
                            "qty": row[3],
                            "status": row[4],
                            "filled_qty": _to_optional_float(row[5]),
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
        alpaca_order_id: Optional[str] = None,
    ) -> None:
        """Update order status in SQLite to prevent duplicate updates.

        Args:
            client_order_id: The client order ID
            status: New status from Alpaca
            filled_qty: Filled quantity (optional, None to preserve DB value)
            filled_avg_price: Filled average price (optional, None to preserve DB value)
            alpaca_order_id: The Alpaca order ID (optional, None to preserve DB value)
        """
        try:
            # Route persistence through the centralized StateStore so both
            # SDK-driven and polling-driven updates follow the same codepath.
            # Only update filled_qty/filled_avg_price if not None; else preserve DB value.
            from src.utils import parse_optional_float

            # Keep `filled_qty` as Optional[float] (None allowed) so that
            # StateStore can decide whether to overwrite the DB value.
            # We intentionally do NOT coerce None -> 0.0 here; the SQL
            # uses `COALESCE(?, filled_qty)` to preserve existing values
            # when the Alpaca response omits the field.
            qty_float: Optional[float] = parse_optional_float(filled_qty)
            self._get_state_store().update_order_intent(
                client_order_id=client_order_id,
                status=status,
                filled_qty=qty_float,
                alpaca_order_id=alpaca_order_id,
                filled_avg_price=(
                    parse_optional_float(filled_avg_price) if filled_avg_price is not None else None
                ),
            )
        except Exception as e:
            logger.error(f"Failed to persist order {client_order_id} via StateStore: {e}")

    def _create_order_update_event(self, alpaca_order: Any) -> OrderUpdateWrapper:
        """Wrap Alpaca order data in an OrderUpdateWrapper compatible with raw update handlers.

        Args:
            alpaca_order: Order data from Alpaca API (dict or Order object)

        Returns:
            OrderUpdateWrapper: A raw-update-compatible wrapper that mimics the Alpaca SDK
                order update interface expected by downstream handlers.
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

            # Get bars from last 5 minutes to ensure fresh data.
            # start=None can return stale cached data, so we use an explicit
            # time window. Request a small window ending at 'now' and request
            # a few bars, then select the newest by timestamp to be robust
            # against ordering/sort semantics of different data backends.
            start_time = datetime.now(timezone.utc) - timedelta(minutes=5)
            end_time = datetime.now(timezone.utc)

            logger.debug(f"Polling batch of {len(symbols)} symbols: {symbols}")

            request = StockBarsRequest(
                symbol_or_symbols=symbols,
                timeframe=TimeFrame.Minute,
                start=start_time,
                end=end_time,
                limit=5,  # Request a few bars and pick the newest explicitly
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

        # Bars may be returned in either ascending or descending order depending
        # on the data backend and request parameters. Choose the bar with the
        # maximum timestamp to ensure we always process the newest bar.
        # Use epoch as a safe fallback so the key always returns a datetime
        epoch = datetime.fromtimestamp(0, timezone.utc)
        latest_bar = max(bar_list, key=lambda b: getattr(b, "timestamp", epoch))
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
