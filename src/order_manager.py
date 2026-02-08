"""Order manager - submission, tracking, idempotency.

Key responsibilities:
- Deterministic client_order_id generation
- Check for duplicates before submission
- Persist order intent before submitting (crash safety)
- Track lifecycle: submitted → filled/rejected/expired/cancelled

Uses float at module boundaries for API compatibility.
"""

import hashlib
import logging
from datetime import datetime, timezone
from typing import Any

from src.broker import Broker
from src.event_bus import EventBus, OrderIntentEvent, SignalEvent
from src.state_store import StateStore

logger = logging.getLogger(__name__)


class OrderManagerError(Exception):
    """Raised when order operation fails."""

    pass


class OrderManager:
    """Order submission and tracking."""

    def __init__(
        self,
        broker: Broker,
        state_store: StateStore,
        event_bus: EventBus,
        config: dict[str, Any],
        strategy_name: str,
        timeframe: str = "1Min",
    ) -> None:
        """Initialise order manager.

        Args:
            broker: Broker client
            state_store: State store
            event_bus: Event bus
            config: Config dict
            strategy_name: Strategy name (for deterministic order ID)
            timeframe: Bar timeframe (for deterministic order ID)
        """
        self.broker = broker
        self.state_store = state_store
        self.event_bus = event_bus
        self.config = config
        self.strategy_name = strategy_name
        self.timeframe = timeframe

        # Execution config
        execution_config = config.get("execution", {})
        self.order_type = execution_config.get("order_type", "market")
        self.time_in_force = execution_config.get("time_in_force", "day")

    def _generate_client_order_id(
        self,
        symbol: str,
        signal_ts: datetime,
        side: str,
    ) -> str:
        """Generate deterministic client_order_id.

        SHA-256(strategy, symbol, timeframe, signal_ts, side)[:16]

        Args:
            symbol: Stock symbol
            signal_ts: Signal timestamp (UTC)
            side: "buy" or "sell"

        Returns:
            16-char hex string
        """
        # Normalize side to prevent duplicate orders from formatting differences
        normalized_side = side.strip().lower()
        data = f"{self.strategy_name}:{symbol}:{self.timeframe}:{signal_ts.isoformat()}:{normalized_side}"
        hash_val = hashlib.sha256(data.encode()).hexdigest()[:16]
        return hash_val

    async def submit_order(
        self,
        signal: SignalEvent,
        qty: float,
    ) -> bool:
        """Submit order from signal.

        Args:
            signal: SignalEvent from strategy
            qty: Order quantity (as float at module boundary)

        Returns:
            True if submitted, False if duplicate prevented

        Raises:
            OrderManagerError if submission fails
        """
        symbol = signal.symbol
        side = "buy" if signal.signal_type == "BUY" else "sell"

        # Log signal metadata (Win #2: multi-timeframe SMA + regime detection)
        sma_period = signal.metadata.get("sma_period", (10, 30))
        confidence = signal.metadata.get("confidence", 0.5)
        regime = signal.metadata.get("regime", "unknown")

        logger.info(
            f"Trading {symbol}: {side.upper()} "
            f"SMA{sma_period} confidence={confidence:.2f} regime={regime}"
        )

        # Generate deterministic order ID
        client_order_id = self._generate_client_order_id(
            symbol=symbol,
            signal_ts=signal.timestamp,
            side=side,
        )

        # Check for duplicate
        existing = self.state_store.get_order_intent(client_order_id)
        if existing:
            logger.info(f"Duplicate order prevented: {client_order_id}")
            return False

        # Persist order intent BEFORE submission (crash safety)
        self.state_store.save_order_intent(
            client_order_id=client_order_id,
            symbol=symbol,
            side=side,
            qty=float(qty),  # Convert Decimal to float for storage
            atr=signal.metadata.get("atr") if hasattr(signal, "metadata") else None,
            status="new",
        )

        # DRY_RUN mode
        if self.config.get("DRY_RUN", False):
            logger.info(
                f"[DRY RUN] Would submit: {symbol} {side} {qty} (order_id: {client_order_id})"
            )
            return True

        # Submit order
        try:
            order = self.broker.submit_order(
                symbol=symbol,
                side=side,
                qty=float(qty),  # Convert Decimal to float for API
                client_order_id=client_order_id,
                order_type=self.order_type,
                time_in_force=self.time_in_force,
            )

            # Update with Alpaca order ID
            self.state_store.update_order_intent(
                client_order_id=client_order_id,
                status="submitted",
                filled_qty=0,
                alpaca_order_id=order.get("id"),
            )

            # Publish order intent event
            await self.event_bus.publish(
                OrderIntentEvent(
                    symbol=symbol,
                    side=side,
                    qty=float(qty),  # Convert Decimal to float for event
                    client_order_id=client_order_id,
                    timestamp=datetime.now(timezone.utc),
                )
            )

            logger.info(f"Order submitted: {client_order_id} (alpaca_id: {order.get('id')})")
            return True
        except Exception as e:
            # Increment circuit breaker on failure (Win #3: persisted)
            cb_failures = self.state_store.get_circuit_breaker_count()
            cb_failures += 1
            self.state_store.save_circuit_breaker_count(cb_failures)  # Win #3: persisted

            if cb_failures >= 5:
                self.state_store.set_state("circuit_breaker_state", "tripped")
                logger.error(f"Circuit breaker tripped after {cb_failures} failures")

            raise OrderManagerError(f"Order submission failed: {e}")
