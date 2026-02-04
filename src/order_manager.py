"""Order execution with idempotency and lifecycle tracking."""
import hashlib
import logging
from datetime import datetime
from pathlib import Path
from typing import Optional

from src.config import Config
from src.event_bus import SignalEvent, OrderIntentEvent, OrderUpdateEvent
from src.state_store import StateStore
from src.broker import Broker
from src.risk_manager import RiskManager


class OrderManager:
    """Manage order execution with idempotency."""

    def __init__(
        self,
        config: Config,
        state_store: StateStore,
        broker: Broker,
        risk_manager: RiskManager,
        logger: logging.Logger,
    ):
        """
        Set up order manager.

        Args:
            config: Configuration object
            state_store: State store instance
            broker: Broker instance
            risk_manager: Risk manager instance
            logger: Logger instance
        """
        self.config = config
        self.state_store = state_store
        self.broker = broker
        self.risk_manager = risk_manager
        self.logger = logger

    def generate_client_order_id(
        self,
        strategy: str,
        symbol: str,
        timeframe: str,
        signal_ts: datetime,
        side: str,
    ) -> str:
        """
        Generate deterministic client_order_id.

        Args:
            strategy: Strategy name
            symbol: Stock symbol
            timeframe: Bar timeframe
            signal_ts: Signal timestamp
            side: Order side (buy/sell)

        Returns:
            Deterministic client order ID (first 16 chars of SHA256 hash)
        """
        # Create hash input
        hash_input = f"{strategy}:{symbol}:{timeframe}:{signal_ts.isoformat()}:{side.lower()}"

        # Generate SHA256 hash
        hash_obj = hashlib.sha256(hash_input.encode())
        hash_hex = hash_obj.hexdigest()

        # Return first 16 characters
        return hash_hex[:16]

    async def process_signal(self, signal: SignalEvent) -> Optional[OrderIntentEvent]:
        """
        Process trading signal and execute order if validated.

        Args:
            signal: Signal event from strategy

        Returns:
            OrderIntentEvent if order submitted, None otherwise
        """
        symbol = signal.symbol
        side = signal.side.lower()

        self.logger.info(
            f"Processing {side.upper()} signal for {symbol}",
            extra={
                "symbol": symbol,
                "side": side,
                "strategy": signal.strategy_name,
            }
        )

        try:
            # Get current price from signal metadata or recent close
            current_price = signal.metadata.get("close", 0) if signal.metadata else 0

            if current_price <= 0:
                self.logger.error(f"Invalid price for {symbol}: {current_price}")
                self.risk_manager.record_failure()
                return None

            # Validate signal
            is_valid, reason = await self.risk_manager.validate_signal(signal, current_price)

            if not is_valid:
                self.logger.warning(f"Signal rejected: {reason}", extra={"symbol": symbol, "side": side})
                return None

            # Get account for position sizing
            account = await self.broker.get_account()
            equity = account.get("equity", 0)

            # Calculate quantity
            qty = self.risk_manager.calculate_qty(equity, current_price)

            if qty <= 0:
                self.logger.warning(f"Calculated quantity is zero for {symbol}")
                return None

            # Generate deterministic client_order_id
            client_order_id = self.generate_client_order_id(
                strategy=signal.strategy_name,
                symbol=symbol,
                timeframe=self.config.bar_timeframe,
                signal_ts=signal.signal_timestamp,
                side=side,
            )

            # Check if order already exists
            if self.state_store.order_exists(client_order_id):
                self.logger.info(
                    f"Duplicate order prevented: {client_order_id}",
                    extra={"client_order_id": client_order_id, "symbol": symbol}
                )
                return None

            # Save order intent BEFORE submitting
            self.state_store.save_order_intent(
                client_order_id=client_order_id,
                symbol=symbol,
                side=side,
                qty=qty,
                status="pending",
            )

            # Submit order (unless dry run)
            if self.config.dry_run:
                self.logger.info(
                    f"[DRY RUN] Would submit {side.upper()} order: {symbol} x{qty} @ ${current_price:.2f}",
                    extra={
                        "client_order_id": client_order_id,
                        "symbol": symbol,
                        "side": side,
                        "qty": qty,
                        "price": current_price,
                    }
                )

                # Update status
                self.state_store.update_order_status(
                    client_order_id=client_order_id,
                    status="dry_run",
                    filled_qty=0,
                )

            else:
                # CRITICAL SAFETY: Re-check kill switch file immediately before order submission
                # This catches kill switch activated AFTER bot startup
                kill_switch_file = Path(__file__).parent.parent / ".kill_switch"
                if kill_switch_file.exists():
                    self.logger.warning(
                        "Kill switch file detected immediately before order - aborting",
                        extra={"client_order_id": client_order_id, "symbol": symbol}
                    )
                    self.state_store.update_order_status(
                        client_order_id=client_order_id,
                        status="blocked_kill_switch",
                        filled_qty=0,
                    )
                    return None

                # Submit real order
                order = await self.broker.submit_order(
                    symbol=symbol,
                    side=side,
                    qty=qty,
                    client_order_id=client_order_id,
                )

                self.logger.info(
                    f"Order submitted: {side.upper()} {symbol} x{qty}",
                    extra={
                        "client_order_id": client_order_id,
                        "alpaca_order_id": order["id"],
                        "symbol": symbol,
                        "side": side,
                        "qty": qty,
                        "status": order["status"],
                    }
                )

                # Update state with Alpaca order ID
                self.state_store.update_order_status(
                    client_order_id=client_order_id,
                    status=order["status"],
                    filled_qty=order.get("filled_qty", 0),
                    alpaca_order_id=order["id"],
                )

                # Record trade if filled
                if order["status"] == "filled":
                    await self._record_trade(
                        client_order_id=client_order_id,
                        symbol=symbol,
                        side=side,
                        qty=qty,
                        price=current_price,
                        order_id=order["id"],
                    )

            # Increment daily trade count
            self.state_store.increment_daily_trade_count()

            # Reset failures on success
            self.risk_manager.reset_failures()

            # Create order intent event
            order_intent = OrderIntentEvent(
                client_order_id=client_order_id,
                symbol=symbol,
                side=side,
                qty=qty,
            )

            return order_intent

        except Exception:
            self.logger.exception("Error processing signal", extra={"symbol": symbol})
            self.risk_manager.record_failure()
            return None

    async def _record_trade(
        self,
        client_order_id: str,
        symbol: str,
        side: str,
        qty: float,
        price: float,
        order_id: str,
    ):
        """Record completed trade."""
        self.state_store.save_trade(
            symbol=symbol,
            side=side,
            qty=qty,
            price=price,
            client_order_id=client_order_id,
            order_id=order_id,
        )

        self.logger.info(
            f"Trade recorded: {side.upper()} {symbol} x{qty} @ ${price:.2f}",
            extra={
                "client_order_id": client_order_id,
                "order_id": order_id,
                "symbol": symbol,
                "side": side,
                "qty": qty,
                "price": price,
            }
        )
