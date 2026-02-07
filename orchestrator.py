"""Orchestrator: Phase-based trading bot initialisation.

This module provides a clean, phase-based approach to initialising
the Alpaca trading bot. It replaces the monolithic main() with
structured phases matching the agent contracts in agents/.
"""

import asyncio
import logging
import signal
import sys
from typing import Optional

from src.config import load_env, load_trading_config, validate_config
from src.broker import Broker
from src.state_store import StateStore
from src.reconciliation import reconcile, ReconciliationError
from src.event_bus import EventBus
from src.stream_polling import StreamPolling  # Consistent with main.py - HTTP polling
from src.data_handler import DataHandler
from src.strategy.sma_crossover import SMACrossover
from src.risk_manager import RiskManager
from src.order_manager import OrderManager
from src.housekeeping import Housekeeping
from src.position_tracker import PositionTracker
from src.exit_manager import ExitManager
from src.logger import setup_logger
from src.alpaca_api.market_data import MarketDataClient
from src.alpaca_api.assets import AssetsClient


logger = logging.getLogger("alpaca_bot")


class Orchestrator:
    """Phase-based trading bot orchestrator.
    
    This class implements the agent phases described in agents/:
    - Phase 1: Infrastructure (matches infrastructure.md contract)
    - Phase 2: Data Layer (matches data_layer.md contract)
    - Phase 3: Trading Logic (matches trading.md contract)
    - Phase 4: Runtime (event processing loop)
    
    Note: The agents/ markdown files describe isolated JSON task/response
    workers as an architectural concept. This orchestrator implements the
    same phase boundaries but as direct method calls rather than isolated
    workers, which is more practical for a single-process Python application.
    """
    
    def __init__(self):
        # Phase 1: Infrastructure components
        self.env: Optional[dict] = None
        self.trading_config: Optional[dict] = None
        self.broker: Optional[Broker] = None
        self.state_store: Optional[StateStore] = None
        
        # Phase 2: Data Layer components
        self.event_bus: Optional[EventBus] = None
        self.stream: Optional[StreamPolling] = None
        self.data_handler: Optional[DataHandler] = None
        self.market_data_client: Optional[MarketDataClient] = None
        self.assets_client: Optional[AssetsClient] = None
        
        # Phase 3: Trading Logic components
        self.strategy: Optional[SMACrossover] = None
        self.risk_manager: Optional[RiskManager] = None
        self.order_manager: Optional[OrderManager] = None
        self.housekeeping: Optional[Housekeeping] = None
        self.position_tracker: Optional[PositionTracker] = None
        self.exit_manager: Optional[ExitManager] = None
        
        # Phase 4: Runtime state
        self.symbols: list[str] = []
        self._shutdown_event = asyncio.Event()
        self._tasks: list[asyncio.Task] = []
    
    async def phase1_infrastructure(self) -> dict:
        """Phase 1: Infrastructure Agent - Load config, init broker, reconcile.
        
        Matches the contract in agents/infrastructure.md and
        agents/infrastructure-worker.md (JSON schema output).
        
        Returns:
            dict: Infrastructure status matching infrastructure-worker.md output schema
        """
        logger.info("=" * 60)
        logger.info("PHASE 1: Infrastructure Initialization")
        logger.info("=" * 60)
        
        errors = []
        warnings = []
        
        try:
            # Load environment
            logger.info("Loading environment...")
            self.env = load_env()
            logger.info(f"   Env loaded (api_key={self.env['ALPACA_API_KEY'][:10]}...)")
            
            # Load trading config
            logger.info("Loading trading config...")
            self.trading_config = load_trading_config(self.env["CONFIG_PATH"])
            strategy_name = self.trading_config.get('strategy', {}).get('name', 'unknown')
            logger.info(f"   Config loaded (strategy={strategy_name})")
            
            # Validate config
            logger.info("Validating config...")
            validate_config(self.env, self.trading_config)
            logger.info("   Config valid")
            
            # Initialize broker
            logger.info("Connecting to broker...")
            self.broker = Broker(
                api_key=self.env["ALPACA_API_KEY"],
                secret_key=self.env["ALPACA_SECRET_KEY"],
                paper=self.env["ALPACA_PAPER"],
            )
            account = self.broker.get_account()
            logger.info("   Broker connected")
            logger.info(f"      Equity: ${account['equity']:,.2f}")
            logger.info(f"      Buying Power: ${account['buying_power']:,.2f}")
            logger.info(f"      Cash: ${account['cash']:,.2f}")
            
            # Initialize state store
            logger.info("Initializing state store...")
            self.state_store = StateStore(self.env["DATABASE_PATH"])
            logger.info(f"   State store ready ({self.env['DATABASE_PATH']})")
            
            # Run reconciliation
            logger.info("Running reconciliation...")
            try:
                reconcile(self.broker, self.state_store)
                logger.info("   Account synced (no discrepancies)")
                reconciliation_status = "clean"
                discrepancies = []
            except ReconciliationError as e:
                logger.error(f"   Reconciliation failed: {e}")
                errors.append(f"Reconciliation failed: {e}")
                reconciliation_status = "discrepancies_found"
                discrepancies = [{"issue": str(e), "sqlite_value": "unknown", "alpaca_value": "unknown"}]
            
            # Get risk config
            risk_config = self.trading_config.get("risk", {})
            
            result = {
                "status": "failed" if errors else "ready",
                "account": {
                    "equity": float(account["equity"]),
                    "buying_power": float(account["buying_power"]),
                    "cash": float(account["cash"]),
                    "mode": "paper" if self.env["ALPACA_PAPER"] else "live",
                },
                "config": {
                    "strategy": strategy_name,
                    "symbols": self.trading_config.get("symbols", {}).get("list", []),
                    "risk": {
                        "kill_switch": risk_config.get("kill_switch", False),
                        "circuit_breaker_limit": risk_config.get("circuit_breaker_limit", 5),
                        "daily_loss_limit_pct": risk_config.get("daily_loss_limit_pct", 5.0),
                        "daily_trade_count_limit": risk_config.get("daily_trade_count_limit", 20),
                        "spread_filter_enabled": risk_config.get("spread_filter_enabled", True),
                    },
                },
                "reconciliation": {
                    "status": reconciliation_status,
                    "discrepancies": discrepancies,
                },
            }
            
            if errors:
                result["errors"] = errors
            if warnings:
                result["warnings"] = warnings
            
            logger.info("Phase 1 complete")
            return result
            
        except Exception as e:
            logger.error(f"Infrastructure phase failed: {e}")
            raise
    
    async def phase2_data_layer(self) -> dict:
        """Phase 2: Data Layer Agent - Connect to stream, prepare data handling.
        
        Matches the contract in agents/data_layer.md.
        Uses StreamPolling (HTTP) instead of Stream (WebSocket) for consistency
        with main.py and to avoid connection limits.
        
        Returns:
            dict: Data layer status
        """
        logger.info("=" * 60)
        logger.info("PHASE 2: Data Layer Initialization")
        logger.info("=" * 60)
        
        try:
            # Initialize event bus
            logger.info("Initializing event bus...")
            self.event_bus = EventBus()
            await self.event_bus.start()
            logger.info("   Event bus ready")
            
            # Initialize stream manager (using polling for consistency)
            logger.info("Preparing stream (polling mode)...")
            self.stream = StreamPolling(
                api_key=self.env["ALPACA_API_KEY"],
                secret_key=self.env["ALPACA_SECRET_KEY"],
                paper=self.env["ALPACA_PAPER"],
                feed=self.trading_config.get("trading", {}).get("stream_feed", "iex"),
            )
            logger.info("   Stream ready (HTTP polling)")
            
            # Initialize data clients
            logger.info("Initializing data clients...")
            self.market_data_client = MarketDataClient(
                api_key=self.env["ALPACA_API_KEY"],
                secret_key=self.env["ALPACA_SECRET_KEY"],
            )
            self.assets_client = AssetsClient(
                api_key=self.env["ALPACA_API_KEY"],
                secret_key=self.env["ALPACA_SECRET_KEY"],
            )
            
            # Initialize data handler
            logger.info("Initializing data handler...")
            self.data_handler = DataHandler(
                state_store=self.state_store,
                event_bus=self.event_bus,
                market_data_client=self.market_data_client,
            )
            logger.info("   Data handler ready")
            
            logger.info("Phase 2 complete")
            return {
                "status": "ready",
                "streams": "polling",
                "event_bus": "active",
            }
            
        except Exception as e:
            logger.error(f"Data layer phase failed: {e}")
            raise
    
    async def phase3_trading_logic(self) -> dict:
        """Phase 3: Trading Logic Agent - Init strategy, risk, order managers.
        
        Matches the contract in agents/trading.md.
        
        Returns:
            dict: Trading logic status
        """
        logger.info("=" * 60)
        logger.info("PHASE 3: Trading Logic Initialization")
        logger.info("=" * 60)
        
        try:
            # Validate symbols
            logger.info("Validating symbols...")
            symbols_config = self.trading_config.get("symbols", {})
            mode = symbols_config.get("mode", "explicit")
            
            if mode == "explicit":
                self.symbols = symbols_config.get("list", [])
            elif mode == "watchlist":
                self.symbols = self.assets_client.get_watchlist(symbols_config.get("watchlist_name"))
            else:
                raise ValueError(f"Symbol mode {mode} not yet implemented")
            
            # Validate all symbols are US equities
            self.symbols = self.assets_client.validate_symbols(self.symbols)
            logger.info(f"   Trading symbols: {self.symbols}")
            
            # Initialize strategy
            logger.info("Initializing strategy...")
            strategy_config = self.trading_config.get("strategy", {})
            strategy_name = strategy_config.get("name")
            
            if strategy_name == "sma_crossover":
                # Multi-timeframe SMA (no longer accepts fast_period/slow_period)
                crypto_symbols = symbols_config.get("crypto_symbols", [])
                self.strategy = SMACrossover(
                    state_store=self.state_store,
                    crypto_symbols=crypto_symbols,
                )
            else:
                raise ValueError(f"Unknown strategy: {strategy_name}")
            
            logger.info(f"   Strategy ready ({strategy_name})")
            
            # Initialize risk manager
            logger.info("Initializing risk manager...")
            self.risk_manager = RiskManager(
                broker=self.broker,
                data_handler=self.data_handler,
                state_store=self.state_store,
                config=self.trading_config,
            )
            logger.info("   Risk manager ready")
            
            # Initialize order manager
            logger.info("Initializing order manager...")
            self.order_manager = OrderManager(
                broker=self.broker,
                state_store=self.state_store,
                event_bus=self.event_bus,
                config=self.trading_config,
                strategy_name=strategy_name,
            )
            logger.info("   Order manager ready")
            
            # Initialize housekeeping
            logger.info("Initializing housekeeping...")
            self.housekeeping = Housekeeping(self.broker, self.state_store)
            logger.info("   Housekeeping ready")
            
            # Initialize position tracker
            logger.info("Initializing position tracker...")
            exits_config = self.trading_config.get("exits", {})
            self.position_tracker = PositionTracker(
                broker=self.broker,
                state_store=self.state_store,
                trailing_stop_enabled=exits_config.get("trailing_stop_enabled", False),
                trailing_stop_activation_pct=exits_config.get("trailing_stop_activation_pct", 0.01),
                trailing_stop_trail_pct=exits_config.get("trailing_stop_trail_pct", 0.005),
            )
            # Load any persisted positions
            self.position_tracker.load_persisted_positions()
            # Sync with broker
            await self.position_tracker.sync_with_broker()
            logger.info("   Position tracker ready")
            
            # Initialize exit manager
            logger.info("Initializing exit manager...")
            self.exit_manager = ExitManager(
                broker=self.broker,
                position_tracker=self.position_tracker,
                event_bus=self.event_bus,
                state_store=self.state_store,
                data_handler=self.data_handler,
                stop_loss_pct=exits_config.get("stop_loss_pct", 0.01),
                profit_target_pct=exits_config.get("profit_target_pct", 0.02),
                trailing_stop_enabled=exits_config.get("trailing_stop_enabled", False),
                trailing_stop_activation_pct=exits_config.get("trailing_stop_activation_pct", 0.01),
                trailing_stop_trail_pct=exits_config.get("trailing_stop_trail_pct", 0.005),
                check_interval_seconds=exits_config.get("check_interval_seconds", 30),
                exit_on_circuit_breaker=exits_config.get("exit_on_circuit_breaker", True),
            )
            logger.info("   Exit manager ready")
            
            logger.info("Phase 3 complete")
            return {
                "status": "ready",
                "strategy": strategy_name,
                "risk_gates": "armed",
                "order_manager": "active",
                "exit_manager": "active",
            }
            
        except Exception as e:
            logger.error(f"Trading logic phase failed: {e}")
            raise
    
    async def phase4_runtime(self) -> None:
        """Phase 4: Runtime - Start streams and event processing.
        
        This is the main runtime loop that wasn't part of the original
        agent contracts but is necessary for the bot to actually run.
        """
        logger.info("=" * 60)
        logger.info("PHASE 4: Runtime - Starting Event Loop")
        logger.info("=" * 60)
        
        # Register stream handlers
        self.stream.register_handlers(
            on_bar=self.data_handler.on_bar,
            on_order_update=self.data_handler.on_order_update,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )
        
        # Start streams
        logger.info("Starting streams (HTTP polling)...")
        await self.stream.start(self.symbols)
        logger.info("Polling stream started")
        
        # Start event processor
        logger.info("Starting event processor...")
        event_processor_task = asyncio.create_task(
            self._event_processor(),
            name="event_processor"
        )
        self._tasks.append(event_processor_task)
        
        # Start housekeeping
        logger.info("Starting housekeeping...")
        housekeeping_task = asyncio.create_task(
            self.housekeeping.start(),
            name="housekeeping"
        )
        self._tasks.append(housekeeping_task)
        
        # Start exit manager
        logger.info("Starting exit manager...")
        await self.exit_manager.start()
        
        # Setup signal handlers
        self._setup_signal_handlers()
        
        logger.info("=" * 60)
        logger.info("Trading bot ready")
        logger.info("=" * 60)
        
        # Monitor tasks until shutdown
        try:
            # Include polling task in monitoring
            if self.stream._polling_task:
                self._tasks.append(self.stream._polling_task)
            
            # Wait for shutdown signal
            shutdown_task = asyncio.create_task(self._shutdown_event.wait(), name="shutdown_watcher")
            self._tasks.append(shutdown_task)
            
            # Wait for any task to complete (or shutdown)
            done, pending = await asyncio.wait(
                self._tasks,
                return_when=asyncio.FIRST_COMPLETED
            )
            
            # Check for exceptions
            for task in done:
                if task.cancelled():
                    continue
                exc = task.exception()
                if exc and not isinstance(exc, asyncio.CancelledError):
                    logger.error(f"Task {task.get_name()} failed: {exc}")
                    raise exc
            
            # Cancel remaining tasks
            for task in pending:
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
                    
        except asyncio.CancelledError:
            logger.info("Runtime cancelled")
        finally:
            await self.graceful_shutdown()
    
    async def _event_processor(self) -> None:
        """Process events from event bus."""
        logger.info("Event processor started")
        iteration = 0
        
        try:
            while True:
                iteration += 1
                if iteration % 100 == 0:
                    logger.info(f"Event processor: iteration {iteration}")
                
                event = await self.event_bus.subscribe()
                
                if event is None:
                    continue
                
                try:
                    from src.event_bus import BarEvent, SignalEvent, ExitSignalEvent
                    
                    if isinstance(event, BarEvent):
                        # Check sufficient history
                        if not self.data_handler.has_sufficient_history(
                            event.symbol,
                            min_bars=self.strategy.get_required_history(),
                        ):
                            continue
                        
                        # Get strategy signal
                        df = self.data_handler.get_dataframe(event.symbol)
                        signals = await self.strategy.on_bar(event.symbol, df)
                        
                        if not signals:
                            continue
                        
                        # Process each signal (multi-timeframe can emit 0-3)
                        for signal in signals:
                            # Check risk
                            try:
                                if not await self.risk_manager.check_signal(signal):
                                    logger.debug(f"Signal filtered: {signal.symbol} {signal.signal_type}")
                                    continue
                            except Exception as e:
                                logger.error(f"Risk check failed: {e}")
                                continue
                            
                            # Submit order
                            try:
                                qty = 1  # TODO: Calculate position size
                                await self.order_manager.submit_order(signal, qty)
                            except Exception as e:
                                logger.error(f"Order submission failed: {e}")
                    
                    elif isinstance(event, ExitSignalEvent):
                        # Handle exit signal from exit manager
                        logger.info(
                            f"Processing exit signal: {event.symbol} {event.reason} "
                            f"(P&L: {event.pnl_pct*100:.1f}%)"
                        )
                        
                        # Validate exit with simplified risk check
                        try:
                            await self.risk_manager.check_exit_order(
                                event.symbol, event.side, event.qty
                            )
                        except Exception as e:
                            logger.error(f"Exit order validation failed: {e}")
                            continue
                        
                        # Create a signal event for order manager
                        exit_signal = SignalEvent(
                            symbol=event.symbol,
                            signal_type="SELL" if event.side == "sell" else "BUY",
                            timestamp=event.timestamp,
                            metadata={"reason": event.reason, "is_exit": True},
                        )
                        
                        # Submit exit order
                        try:
                            await self.order_manager.submit_order(exit_signal, event.qty)
                            # Stop tracking position after exit
                            self.position_tracker.stop_tracking(event.symbol)
                        except Exception as e:
                            logger.error(f"Exit order submission failed: {e}")
                
                except Exception as e:
                    logger.error(f"Event processing failed: {e}", exc_info=True)
        
        except asyncio.CancelledError:
            logger.info(f"Event processor cancelled after {iteration} iterations")
            raise
    
    def _setup_signal_handlers(self) -> None:
        """Setup SIGTERM/SIGINT handlers for graceful shutdown."""
        def handle_signal(signum, frame):
            sig_name = "SIGTERM" if signum == signal.SIGTERM else "SIGINT"
            logger.info(f"{sig_name} received - initiating graceful shutdown")
            self._shutdown_event.set()
        
        signal.signal(signal.SIGTERM, handle_signal)
        signal.signal(signal.SIGINT, handle_signal)
        logger.info("Signal handlers registered")
    
    async def graceful_shutdown(self) -> None:
        """Graceful shutdown sequence."""
        logger.info("=" * 60)
        logger.info("Graceful Shutdown Initiated")
        logger.info("=" * 60)
        
        try:
            # Stop exit manager first (to prevent new exit signals during shutdown)
            if self.exit_manager:
                logger.info("Stopping exit manager...")
                await self.exit_manager.stop()
            
            # Stop stream
            if self.stream:
                logger.info("Stopping data stream...")
                await self.stream.stop()
            
            # Cancel open orders
            if self.broker:
                logger.info("Cancelling open orders...")
                try:
                    orders = self.broker.get_open_orders()
                    for order in orders:
                        self.broker.cancel_order(order["id"])
                        logger.info(f"  Cancelled: {order['id']} ({order['symbol']})")
                    if not orders:
                        logger.info("  No open orders to cancel")
                except Exception as e:
                    logger.error(f"Failed to cancel orders: {e}")
                
                # Close positions
                logger.info("Closing open positions...")
                try:
                    positions = self.broker.get_positions()
                    for position in positions:
                        symbol = position["symbol"]
                        qty = position["qty"]
                        side = "sell" if qty > 0 else "buy"
                        abs_qty = abs(qty)
                        logger.info(f"  Closing {symbol}: {abs_qty} shares via {side}")
                        self.broker.submit_order(symbol, abs_qty, side, "market")
                    if not positions:
                        logger.info("  No open positions to close")
                except Exception as e:
                    logger.error(f"Failed to close positions: {e}")
            
            # Stop event bus
            if self.event_bus:
                logger.info("Stopping event bus...")
                await self.event_bus.stop()
            
            logger.info("=" * 60)
            logger.info("Shutdown complete")
            logger.info("=" * 60)
        
        except Exception as e:
            logger.error(f"Shutdown error: {e}", exc_info=True)
    
    async def run(self) -> bool:
        """Execute all phases in sequence.
        
        Returns:
            bool: True if bot started successfully
        """
        try:
            # Phase 1: Infrastructure
            p1 = await self.phase1_infrastructure()
            if p1.get("status") != "ready":
                logger.error("Infrastructure phase failed - cannot continue")
                return False
            
            # Phase 2: Data Layer
            p2 = await self.phase2_data_layer()
            if p2.get("status") != "ready":
                logger.error("Data layer phase failed - cannot continue")
                return False
            
            # Phase 3: Trading Logic
            p3 = await self.phase3_trading_logic()
            if p3.get("status") != "ready":
                logger.error("Trading logic phase failed - cannot continue")
                return False
            
            # Phase 4: Runtime (blocks until shutdown)
            await self.phase4_runtime()
            
            return True
            
        except Exception as e:
            logger.error(f"Orchestration failed: {e}", exc_info=True)
            return False


async def main():
    """Entry point - matches original main.py interface."""
    logger = setup_logger()
    
    logger.info("=" * 60)
    logger.info("Alpaca Trading Bot - Startup")
    logger.info("=" * 60)
    
    orchestrator = Orchestrator()
    success = await orchestrator.run()
    
    return 0 if success else 1


if __name__ == "__main__":
    exit_code = asyncio.run(main())
    sys.exit(exit_code)
