"""WebSocket stream handler with reconnection logic."""
import asyncio
import logging
import random
from datetime import datetime, timedelta
import pytz

from alpaca.data.live import StockDataStream
from alpaca.data.models import Bar

from src.config import Config
from src.event_bus import EventBus, MarketBarEvent
from src.broker import Broker
from src.data_handler import DataHandler


class StreamHandler:
    """Handle Alpaca WebSocket stream with reconnection logic."""

    def __init__(
        self,
        config: Config,
        event_bus: EventBus,
        broker: Broker,
        data_handler: DataHandler,
        logger: logging.Logger,
    ):
        """
        Initialize stream handler.

        Args:
            config: Configuration object
            event_bus: Event bus instance
            broker: Broker instance for backfill
            data_handler: Data handler for backfill
            logger: Logger instance
        """
        self.config = config
        self.event_bus = event_bus
        self.broker = broker
        self.data_handler = data_handler
        self.logger = logger

        self.stream = StockDataStream(
            api_key=config.alpaca_api_key,
            secret_key=config.alpaca_secret_key,
            feed=config.stream_feed,
        )

        self.running = False
        self.last_message_time = None
        self.reconnect_count = 0
        self.max_reconnect_attempts = 10
        self.max_reconnect_delay = 60.0
        self.message_timeout = 30.0
        self.last_bar_times = {}  # Track last bar time per symbol for backfill

    async def _handle_bar(self, bar: Bar):
        """
        Handle incoming bar from WebSocket.

        Args:
            bar: Bar object from Alpaca
        """
        try:
            # Update last message time
            self.last_message_time = datetime.utcnow()

            # Track last bar time for backfill detection
            self.last_bar_times[bar.symbol] = bar.timestamp

            # Create event
            event = MarketBarEvent(
                symbol=bar.symbol,
                open=float(bar.open),
                high=float(bar.high),
                low=float(bar.low),
                close=float(bar.close),
                volume=int(bar.volume),
                bar_timestamp=bar.timestamp,
                vwap=float(bar.vwap) if bar.vwap else None,
            )

            # Publish to event bus
            await self.event_bus.publish(event)

        except Exception as e:
            self.logger.error(f"Error handling bar: {e}", exc_info=e)

    async def _backfill_missed_bars(self):
        """Backfill missed bars after reconnection."""
        self.logger.info("Backfilling missed bars after reconnection")

        for symbol in self.config.symbols:
            try:
                # Determine backfill start time
                if symbol in self.last_bar_times:
                    # Start from last received bar
                    start_time = self.last_bar_times[symbol]
                else:
                    # No previous data, backfill last N bars
                    start_time = datetime.now(pytz.timezone("America/New_York")) - timedelta(hours=1)

                end_time = datetime.now(pytz.timezone("America/New_York"))

                # Fetch bars
                df = await self.broker.get_bars(
                    symbol=symbol,
                    timeframe=self.config.bar_timeframe,
                    start=start_time,
                    end=end_time,
                    limit=100,
                )

                if not df.empty:
                    # Add to data handler
                    self.data_handler.add_historical_bars(symbol, df)
                    self.logger.info(f"Backfilled {len(df)} bars for {symbol}")

                    # Update last bar time
                    self.last_bar_times[symbol] = df.index[-1]

            except Exception as e:
                self.logger.error(f"Error backfilling {symbol}: {e}", exc_info=e)

    async def _monitor_connection(self):
        """Monitor connection health and force reconnect if needed."""
        while self.running:
            try:
                await asyncio.sleep(10)  # Check every 10 seconds

                if self.last_message_time:
                    time_since_last_message = (datetime.utcnow() - self.last_message_time).total_seconds()

                    if time_since_last_message > self.message_timeout:
                        self.logger.warning(
                            f"No messages received for {time_since_last_message:.1f}s, forcing reconnect"
                        )
                        # Stop and restart stream
                        await self._reconnect()

            except Exception as e:
                self.logger.error(f"Error in connection monitor: {e}", exc_info=e)

    async def _reconnect(self):
        """Handle reconnection with exponential backoff."""
        self.reconnect_count += 1

        if self.reconnect_count > self.max_reconnect_attempts:
            self.logger.error(
                f"Max reconnect attempts ({self.max_reconnect_attempts}) exceeded, "
                "tripping circuit breaker"
            )
            # Signal circuit breaker trip
            raise Exception("Max reconnect attempts exceeded")

        # Calculate backoff delay with jitter
        delay = min(
            self.max_reconnect_delay,
            (2 ** (self.reconnect_count - 1)) + random.uniform(0, 1)
        )

        self.logger.info(f"Reconnecting in {delay:.1f}s (attempt {self.reconnect_count})")
        await asyncio.sleep(delay)

        # Stop existing stream
        try:
            await self.stream.stop_ws()
        except:
            pass

        # Backfill missed bars
        await self._backfill_missed_bars()

        # Restart stream
        await self._start_stream()

    async def _start_stream(self):
        """Start the WebSocket stream."""
        # Subscribe to bars
        for symbol in self.config.symbols:
            self.stream.subscribe_bars(self._handle_bar, symbol)

        # Run stream
        self.logger.info(f"Starting WebSocket stream for symbols: {self.config.symbols}")
        try:
            await self.stream._run_forever()
        except Exception as e:
            self.logger.error(f"Stream error: {e}", exc_info=e)
            if self.running:
                await self._reconnect()

    async def run(self):
        """Run the stream handler."""
        self.running = True
        self.last_message_time = datetime.utcnow()

        # Start connection monitor task
        monitor_task = asyncio.create_task(self._monitor_connection())

        try:
            # Start stream
            await self._start_stream()

        except Exception as e:
            self.logger.error(f"Fatal stream error: {e}", exc_info=e)
            raise

        finally:
            # Cleanup
            monitor_task.cancel()
            try:
                await monitor_task
            except asyncio.CancelledError:
                pass

    async def stop(self):
        """Stop the stream handler."""
        self.logger.info("Stopping WebSocket stream")
        self.running = False

        try:
            await self.stream.stop_ws()
        except Exception as e:
            self.logger.warning(f"Error stopping stream: {e}")

    def reset_reconnect_count(self):
        """Reset reconnect count after successful operation."""
        if self.reconnect_count > 0:
            self.logger.info(f"Resetting reconnect count (was {self.reconnect_count})")
            self.reconnect_count = 0
