"""Orchestrator: Phase-based trading bot initialisation.

This module provides a clean, phase-based approach to initialising
the Alpaca trading bot. It replaces the monolithic main() with
structured phases matching the agent contracts in agents/.
"""

import asyncio
import inspect
import logging
import signal
import sys
from typing import Optional

from src.alpaca_api.assets import AssetsClient
from src.alpaca_api.market_data import MarketDataClient
from src.broker import Broker
from src.config import load_env, load_trading_config, validate_config
from src.data_handler import DataHandler
from src.event_bus import BarEvent, EventBus, ExitSignalEvent, OrderUpdateEvent, SignalEvent
from src.exit_manager import ExitManager
from src.housekeeping import Housekeeping
from src.logger import setup_logger
from src.metrics import metrics, write_metrics_to_file
from src.notifier import AlertNotifier
from src.order_manager import OrderManager
from src.position_sizer import calculate_position_size
from src.position_tracker import PositionTracker
from src.reconciliation import ReconciliationError, reconcile
from src.risk_manager import RiskManager
from src.state_store import StateStore
from src.utils import parse_optional_float
from src.strategy.sma_crossover import SMACrossover
from src.stream_polling import StreamPolling  # Consistent with main.py - HTTP polling

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
        self.notifier: Optional[AlertNotifier] = None

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
            logger.info("Loading environment...")
            self.env = load_env()
            logger.info(f"   Env loaded (api_key={self.env['ALPACA_API_KEY'][:10]}...)")

            logger.info("Loading trading config...")
            self.trading_config = load_trading_config(self.env["CONFIG_PATH"])
            strategy_name = self.trading_config.get("strategy", {}).get("name", "unknown")
            logger.info(f"   Config loaded (strategy={strategy_name})")

            logger.info("Validating config...")
            validate_config(self.env, self.trading_config)
            logger.info("   Config valid")

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

            logger.info("Setting up state store...")
            self.state_store = StateStore(self.env["DATABASE_PATH"])
            logger.info(f"   State store ready ({self.env['DATABASE_PATH']})")

            # Tier 1 alerts
            logger.info("Starting alert notifier...")
            alert_config = self.trading_config.get("alerts", {})
            self.notifier = AlertNotifier(
                alert_channel=alert_config.get("channel"),
                alert_target=alert_config.get("target"),
            )
            if self.notifier.enabled:
                logger.info(f"   Notifier ready ({alert_config.get('channel')})")
            else:
                logger.info("   Notifier ready (logging only)")

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
                discrepancies = [
                    {"issue": str(e), "sqlite_value": "unknown", "alpaca_value": "unknown"}
                ]

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
            # Start event bus
            logger.info("Starting event bus...")
            self.event_bus = EventBus()
            await self.event_bus.start()
            logger.info("   Event bus ready")

            # Start stream manager (using polling for consistency)
            logger.info("Preparing stream (polling mode)...")
            self.stream = StreamPolling(
                api_key=self.env["ALPACA_API_KEY"],
                secret_key=self.env["ALPACA_SECRET_KEY"],
                paper=self.env["ALPACA_PAPER"],
                feed=self.trading_config.get("trading", {}).get("stream_feed", "iex"),
            )
            logger.info("   Stream ready (HTTP polling)")

            # Start data clients
            logger.info("Starting data clients...")
            self.market_data_client = MarketDataClient(
                api_key=self.env["ALPACA_API_KEY"],
                secret_key=self.env["ALPACA_SECRET_KEY"],
            )
            self.assets_client = AssetsClient(
                api_key=self.env["ALPACA_API_KEY"],
                secret_key=self.env["ALPACA_SECRET_KEY"],
            )

            logger.info("Starting data handler...")
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
                self.symbols = self.assets_client.get_watchlist(
                    symbols_config.get("watchlist_name")
                )
            else:
                raise ValueError(f"Symbol mode {mode} not yet implemented")

            # Validate all symbols are US equities
            self.symbols = self.assets_client.validate_symbols(self.symbols)
            logger.info(f"   Trading symbols: {self.symbols}")

            # Initialise strategy
            logger.info("Initialising strategy...")
            strategy_config = self.trading_config.get("strategy", {})
            strategy_name = strategy_config.get("name")

            if strategy_name == "sma_crossover":
                # Multi-timeframe SMA (no longer accepts fast_period/slow_period)
                # Note: Deduplication moved to OrderManager (separation of concerns)
                crypto_symbols = symbols_config.get("crypto_symbols", [])
                self.strategy = SMACrossover(
                    crypto_symbols=crypto_symbols,
                )
            else:
                raise ValueError(f"Unknown strategy: {strategy_name}")

            logger.info(f"   Strategy ready ({strategy_name})")

            # Initialise risk manager
            logger.info("Initialising risk manager...")
            self.risk_manager = RiskManager(
                broker=self.broker,
                data_handler=self.data_handler,
                state_store=self.state_store,
                config=self.trading_config,
            )
            logger.info("   Risk manager ready")

            # Initialise order manager
            logger.info("Initialising order manager...")
            self.order_manager = OrderManager(
                broker=self.broker,
                state_store=self.state_store,
                event_bus=self.event_bus,
                config=self.trading_config,
                strategy_name=strategy_name,
            )
            logger.info("   Order manager ready")

            # Initialise housekeeping
            logger.info("Initialising housekeeping...")
            self.housekeeping = Housekeeping(self.broker, self.state_store)
            logger.info("   Housekeeping ready")

            # Initialise position tracker
            logger.info("Initialising position tracker...")
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

            # Initialise exit manager
            logger.info("Initialising exit manager...")

            # Exit configuration validated earlier in `validate_config()`
            exits_config = self.trading_config.get("exits", {})

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
        event_processor_task = asyncio.create_task(self._event_processor(), name="event_processor")
        self._tasks.append(event_processor_task)

        # Start housekeeping
        logger.info("Starting housekeeping...")
        housekeeping_task = asyncio.create_task(self.housekeeping.start(), name="housekeeping")
        self._tasks.append(housekeeping_task)

        # Start exit manager
        logger.info("Starting exit manager...")
        await self.exit_manager.start()

        # Start metrics writer
        logger.info("Starting metrics writer...")
        metrics_task = asyncio.create_task(self._metrics_writer(), name="metrics_writer")
        self._tasks.append(metrics_task)

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
            shutdown_task = asyncio.create_task(
                self._shutdown_event.wait(), name="shutdown_watcher"
            )
            self._tasks.append(shutdown_task)

            # Wait for any task to complete (or shutdown)
            done, pending = await asyncio.wait(self._tasks, return_when=asyncio.FIRST_COMPLETED)

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
                            # Track signal generated
                            metrics.record_signal_generated()

                            # Check risk
                            try:
                                if not await self.risk_manager.check_signal(signal):
                                    logger.debug(
                                        f"Signal filtered: {signal.symbol} {signal.signal_type}"
                                    )
                                    metrics.record_signal_filtered_risk()
                                    continue
                            except Exception as e:
                                logger.error(f"Risk check failed: {e}")
                                metrics.record_signal_filtered_risk()
                                continue

                            # Submit order with position sizing
                            try:
                                # Get current price from latest bar
                                current_price = (
                                    float(df["close"].iloc[-1])
                                    if df is not None and not df.empty
                                    else 0.0
                                )

                                # Get account equity
                                account = self.broker.get_account()
                                account_equity = float(account.get("equity", 0))

                                # Get risk config with defaults
                                risk_config = self.trading_config.get("risk", {})

                                # Calculate position size
                                qty = calculate_position_size(
                                    symbol=signal.symbol,
                                    side=signal.signal_type.lower(),
                                    account_equity=account_equity,
                                    current_price=current_price,
                                    max_position_pct=risk_config.get("max_position_pct", 0.10),
                                    max_risk_per_trade_pct=risk_config.get(
                                        "max_risk_per_trade_pct", 0.01
                                    ),
                                    stop_loss_pct=risk_config.get("stop_loss_pct", 0.01),
                                )

                                logger.info(
                                    f"Calculated position size for {signal.symbol}: "
                                    f"{qty} shares at ${current_price:.2f} "
                                    f"(equity: ${account_equity:.2f})"
                                )

                                await self.order_manager.submit_order(signal, qty)
                                metrics.record_order_submitted()
                            except Exception as e:
                                logger.error(f"Order submission failed: {e}")
                                metrics.record_order_rejected()

                    elif isinstance(event, ExitSignalEvent):
                        # Handle exit signal from exit manager
                        logger.info(
                            f"Processing exit signal: {event.symbol} {event.reason} "
                            f"(P&L: {event.pnl_pct*100:.1f}%)"
                        )

                        # Track exit triggered
                        metrics.record_exit_triggered()

                        # Send alert for exit
                        await self.send_critical_alert(
                            "exit_triggered",
                            {
                                "symbol": event.symbol,
                                "reason": event.reason,
                                "pnl_pct": event.pnl_pct,
                                "pnl_amount": event.pnl_amount,
                            },
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
                            metrics.record_order_submitted()
                            # Stop tracking position after exit
                            self.position_tracker.stop_tracking(event.symbol)
                        except Exception as e:
                            logger.error(f"Exit order submission failed: {e}")
                            metrics.record_order_rejected()

                    elif isinstance(event, OrderUpdateEvent):
                        # Handle order fill events for P&L tracking
                        if event.status == "filled":
                            await self._handle_order_fill(event)

                except Exception as e:
                    logger.error(f"Event processing failed: {e}", exc_info=True)

        except asyncio.CancelledError:
            logger.info(f"Event processor cancelled after {iteration} iterations")
            raise

    async def _handle_order_fill(self, event: OrderUpdateEvent) -> None:
        """Handle order fill events for P&L tracking and position management.

        Args:
            event: OrderUpdateEvent with fill details
        """
        # Look up order intent to get side
        order_intent = self.state_store.get_order_intent(event.client_order_id)
        if not order_intent:
            logger.warning(f"Fill received for unknown order: {event.client_order_id}")
            return

        side = order_intent.get("side", "")
        fill_price = event.avg_fill_price

        if side == "buy":
            # Start tracking position with actual fill price
            atr_raw = order_intent.get("atr") if order_intent else None
            atr_value = parse_optional_float(atr_raw)

            self.position_tracker.start_tracking(
                symbol=event.symbol,
                fill_price=fill_price,
                qty=event.filled_qty,
                side="long",
                atr=atr_value,
            )
            logger.info(
                f"Buy fill captured: {event.symbol} @ ${fill_price:.2f} " f"qty={event.filled_qty}"
            )
        elif side == "sell":
            # Calculate realized P&L
            position = self.position_tracker.get_position(event.symbol)
            if position:
                realized_pnl = (fill_price - position.entry_price) * event.filled_qty

                # Update daily P&L
                current_daily_pnl = self.state_store.get_daily_pnl()
                new_daily_pnl = current_daily_pnl + realized_pnl
                self.state_store.save_daily_pnl(new_daily_pnl)

                # Update position tracker
                self.position_tracker.stop_tracking(event.symbol)

                # Increment daily trade count
                count = self.state_store.get_daily_trade_count()
                self.state_store.save_daily_trade_count(count + 1)

                logger.info(
                    f"Sell fill captured: {event.symbol} @ ${fill_price:.2f} "
                    f"qty={event.filled_qty} realized_pnl=${realized_pnl:.2f} "
                    f"daily_pnl=${new_daily_pnl:.2f}"
                )
            else:
                logger.warning(f"Sell fill for untracked position: {event.symbol}")

        # Update metrics gauges
        metrics.record_order_filled()
        metrics.update_daily_pnl(self.state_store.get_daily_pnl())
        metrics.update_daily_trade_count(self.state_store.get_daily_trade_count())

    async def _metrics_writer(self) -> None:
        """Periodically write metrics to file.

        Writes metrics to data/metrics.json every 60 seconds.
        """
        logger.info("Metrics writer started (60s interval)")
        try:
            while True:
                try:
                    # Update gauge values before writing
                    positions = []
                    if self.broker:
                        try:
                            maybe_positions = self.broker.get_positions()
                            if inspect.isawaitable(maybe_positions):
                                positions = await maybe_positions
                            else:
                                positions = maybe_positions
                        except Exception:
                            logger.exception("Failed to read positions from broker; using 0")

                    # Safely compute open positions count
                    open_positions_count = 0
                    try:
                        open_positions_count = len(positions) if positions is not None else 0
                    except Exception:
                        open_positions_count = 0

                    metrics.update_open_positions(open_positions_count)
                    metrics.update_daily_pnl(self.state_store.get_daily_pnl())
                    metrics.update_daily_trade_count(self.state_store.get_daily_trade_count())

                    # Write metrics to file
                    write_metrics_to_file("data/metrics.json")
                    logger.debug("Metrics written to data/metrics.json")
                except Exception:
                    logger.exception("Error in metrics writer loop; continuing")

                await asyncio.sleep(60)
        except asyncio.CancelledError:
            logger.info("Metrics writer cancelled")
            raise

    async def send_critical_alert(self, event_type: str, details: dict) -> bool:
        """Send alert for critical events.

        Args:
            event_type: Type of critical event
            details: Event-specific details

        Returns:
            True if alert sent successfully
        """
        if not self.notifier:
            logger.warning(f"Notifier not initialised, cannot send {event_type} alert")
            return False

        try:
            if event_type == "circuit_breaker_tripped":
                return self.notifier.alert_circuit_breaker_tripped(details["failure_count"])
            elif event_type == "daily_loss_exceeded":
                return self.notifier.alert_daily_loss_limit_exceeded(
                    details["daily_pnl"],
                    details["limit"],
                )
            elif event_type == "exit_triggered":
                return self.notifier.send_alert(
                    title=f"Exit: {details['symbol']} ({details['reason']})",
                    message=f"P&L: {details['pnl_pct']*100:.1f}% (${details['pnl_amount']:.2f})",
                    severity="WARNING" if details["pnl_amount"] < 0 else "INFO",
                )
            elif event_type == "kill_switch_activated":
                return self.notifier.alert_kill_switch_activated()
            else:
                logger.warning(f"Unknown alert event type: {event_type}")
                return False
        except Exception as e:
            logger.error(f"Failed to send {event_type} alert: {e}")
            return False

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
