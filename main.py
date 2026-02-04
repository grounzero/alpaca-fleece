"""Main orchestration for Alpaca trading bot."""
import asyncio
import signal
import sys
from pathlib import Path

from src.config import load_config
from src.logger import setup_logger, RUN_ID
from src.state_store import StateStore
from src.broker import Broker
from src.event_bus import EventBus, EventType
from src.data_handler import DataHandler
from src.strategy.sma_crossover import SMACrossoverStrategy
from src.risk_manager import RiskManager
from src.order_manager import OrderManager
from src.stream import StreamHandler


class TradingBot:
    """Main trading bot orchestrator."""

    def __init__(self):
        """Initialize trading bot."""
        self.config = None
        self.logger = None
        self.state_store = None
        self.broker = None
        self.event_bus = None
        self.data_handler = None
        self.strategy = None
        self.risk_manager = None
        self.order_manager = None
        self.stream_handler = None
        self.running = False
        self.tasks = []

    async def startup(self):
        """Initialize all components."""
        # Load configuration
        try:
            self.config = load_config()
        except Exception as e:
            print(f"ERROR: Configuration failed: {e}")
            sys.exit(1)

        # Setup logger
        self.logger = setup_logger("alpaca-bot", log_level=self.config.log_level)
        self.logger.info(f"Starting Alpaca Trading Bot (run_id: {RUN_ID})")

        # Log trading mode
        if self.config.is_live_trading_enabled():
            self.logger.warning("=" * 60)
            self.logger.warning("LIVE TRADING ENABLED - REAL MONEY AT RISK")
            self.logger.warning("=" * 60)
        else:
            self.logger.info("Paper trading mode")

        if self.config.dry_run:
            self.logger.info("Dry run mode enabled - no orders will be submitted")

        # Initialize state store
        self.state_store = StateStore()
        self.logger.info("State store initialized")

        # Initialize broker
        self.broker = Broker(self.config, self.logger)
        self.logger.info("Broker initialized")

        # Reconcile account state
        await self.reconcile_account()

        # Check circuit breaker
        cb_state = self.state_store.get_circuit_breaker_state()
        if cb_state.get("tripped"):
            self.logger.error(
                "Circuit breaker is TRIPPED. Manual reset required. "
                "Set CIRCUIT_BREAKER_RESET=true or delete circuit breaker state from database."
            )
            sys.exit(1)

        # Reset circuit breaker if requested
        if self.config.circuit_breaker_reset:
            self.logger.info("Resetting circuit breaker (CIRCUIT_BREAKER_RESET=true)")
            self.state_store.reset_circuit_breaker()

        # Initialize event bus
        self.event_bus = EventBus()
        self.logger.info("Event bus initialized")

        # Initialize data handler
        window_size = self.config.sma_slow + 10
        self.data_handler = DataHandler(window_size=window_size)
        self.logger.info(f"Data handler initialized (window_size={window_size})")

        # Initialize strategy
        self.strategy = SMACrossoverStrategy(
            fast_period=self.config.sma_fast,
            slow_period=self.config.sma_slow,
            state_store=self.state_store,
        )
        self.logger.info(f"Strategy initialized: {self.strategy}")

        # Initialize risk manager
        self.risk_manager = RiskManager(
            config=self.config,
            state_store=self.state_store,
            broker=self.broker,
            logger=self.logger,
        )
        self.logger.info("Risk manager initialized")

        # Initialize order manager
        self.order_manager = OrderManager(
            config=self.config,
            state_store=self.state_store,
            broker=self.broker,
            risk_manager=self.risk_manager,
            logger=self.logger,
        )
        self.logger.info("Order manager initialized")

        # Initialize stream handler
        self.stream_handler = StreamHandler(
            config=self.config,
            event_bus=self.event_bus,
            broker=self.broker,
            data_handler=self.data_handler,
            logger=self.logger,
        )
        self.logger.info("Stream handler initialized")

        # Subscribe to events
        self.event_bus.subscribe(EventType.MARKET_BAR, self.on_market_bar)
        self.event_bus.subscribe(EventType.SIGNAL, self.on_signal)

        self.logger.info("Startup complete")

    async def reconcile_account(self):
        """Reconcile account state with Alpaca."""
        self.logger.info("Reconciling account state...")

        try:
            # Get account info
            account = await self.broker.get_account()
            self.logger.info(
                f"Account: equity=${account['equity']:.2f}, cash=${account['cash']:.2f}, "
                f"buying_power=${account['buying_power']:.2f}",
                extra={"account": account}
            )

            # Get positions
            positions = await self.broker.get_positions()
            if positions:
                self.logger.info(f"Open positions: {len(positions)}")
                for pos in positions:
                    self.logger.info(
                        f"  {pos['symbol']}: {pos['qty']} shares @ ${pos['current_price']:.2f} "
                        f"(P&L: ${pos['unrealized_pl']:.2f})",
                        extra={"position": pos}
                    )
            else:
                self.logger.info("No open positions")

            # Get open orders
            orders = await self.broker.get_open_orders()
            if orders:
                self.logger.info(f"Open orders: {len(orders)}")
                for order in orders:
                    self.logger.info(
                        f"  {order['symbol']}: {order['side']} {order['qty']} @ {order['status']}",
                        extra={"order": order}
                    )
            else:
                self.logger.info("No open orders")

            self.logger.info("Account reconciliation complete")

        except Exception as e:
            self.logger.error(f"Account reconciliation failed: {e}", exc_info=e)
            raise

    async def on_market_bar(self, event):
        """Handle market bar events."""
        try:
            # Update data handler
            df = self.data_handler.on_bar(event)

            if df is None or len(df) < self.strategy.get_required_history():
                return

            # Generate signal
            signal = self.strategy.on_bar(event.symbol, df)

            if signal:
                # Publish signal
                await self.event_bus.publish(signal)

        except Exception as e:
            self.logger.error(f"Error handling market bar: {e}", exc_info=e)

    async def on_signal(self, event):
        """Handle signal events."""
        try:
            # Process signal through order manager
            order_intent = await self.order_manager.process_signal(event)

            if order_intent:
                # Publish order intent (for logging/monitoring)
                await self.event_bus.publish(order_intent)

        except Exception as e:
            self.logger.error(f"Error handling signal: {e}", exc_info=e)

    async def housekeeping(self):
        """Periodic housekeeping tasks."""
        interval = 60  # seconds

        while self.running:
            try:
                await asyncio.sleep(interval)

                # Save equity snapshot
                account = await self.broker.get_account()
                equity = account.get("equity", 0)

                self.state_store.save_equity(equity)

                self.logger.info(
                    f"Heartbeat: equity=${equity:.2f}",
                    extra={"equity": equity}
                )

                # Reset stream reconnect count if successful
                self.stream_handler.reset_reconnect_count()

            except asyncio.CancelledError:
                break
            except Exception as e:
                self.logger.error(f"Housekeeping error: {e}", exc_info=e)

    async def run(self):
        """Run the trading bot."""
        self.running = True

        try:
            # Start event bus
            event_bus_task = self.event_bus.start_task()
            self.tasks.append(event_bus_task)

            # Start stream handler
            stream_task = asyncio.create_task(self.stream_handler.run())
            self.tasks.append(stream_task)

            # Start housekeeping
            housekeeping_task = asyncio.create_task(self.housekeeping())
            self.tasks.append(housekeeping_task)

            self.logger.info("All tasks started, bot is running")

            # Wait for tasks
            await asyncio.gather(*self.tasks, return_exceptions=True)

        except Exception as e:
            self.logger.error(f"Fatal error: {e}", exc_info=e)
            raise

    async def shutdown(self):
        """Shutdown the bot gracefully."""
        self.logger.info("Shutting down...")
        self.running = False

        # Stop stream
        if self.stream_handler:
            await self.stream_handler.stop()

        # Stop event bus
        if self.event_bus:
            await self.event_bus.stop(timeout=5.0)

        # Cancel tasks
        for task in self.tasks:
            if not task.done():
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass

        # Close state store
        if self.state_store:
            self.state_store.close()

        self.logger.info("Shutdown complete")


# Global bot instance for signal handling
bot = None


def signal_handler(sig, frame):
    """Handle shutdown signals."""
    if bot:
        asyncio.create_task(bot.shutdown())


async def main():
    """Main entry point."""
    global bot

    bot = TradingBot()

    # Register signal handlers
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    try:
        # Startup
        await bot.startup()

        # Run
        await bot.run()

    except KeyboardInterrupt:
        print("\nInterrupted by user")
    except Exception as e:
        print(f"ERROR: {e}")
        if bot and bot.logger:
            bot.logger.error(f"Fatal error: {e}", exc_info=e)
    finally:
        # Cleanup
        if bot:
            await bot.shutdown()


if __name__ == "__main__":
    asyncio.run(main())
