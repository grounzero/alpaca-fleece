"""Order manager - submission, tracking, idempotency.

Key responsibilities:
- Deterministic client_order_id generation
- Check for duplicates before submission
- Persist order intent before submitting (crash safety)
- Track lifecycle: submitted → filled/rejected/expired/cancelled

Uses float at module boundaries for API compatibility.
"""

import asyncio
import hashlib
import logging
from datetime import datetime, timezone
from typing import Any

from src.broker import Broker
from src.event_bus import EventBus, OrderIntentEvent, SignalEvent
from src.position_tracker import PositionTracker
from src.state_store import StateStore
from src.utils import parse_optional_float

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
        position_tracker: PositionTracker | None = None,
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
        self.position_tracker = position_tracker

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
        # Convert side to a canonical form to prevent duplicate orders from formatting differences
        converted_side = side.strip().lower()
        data = f"{self.strategy_name}:{symbol}:{self.timeframe}:{signal_ts.isoformat()}:{converted_side}"
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

        # Determine whether this signal is an exposure-increasing entry or an exit
        # Map to actions used by the gating table
        action = None
        # NOTE: `submit_order` is async. Calling the broker synchronously here
        # may perform network I/O and block the event loop. If `Broker.get_positions`
        # is I/O-bound consider one of the following to avoid blocking:
        # - make `get_positions` an async method and `await` it here,
        # - call it in a thread with `await asyncio.to_thread(self.broker.get_positions)`,
        # - or cache recent positions elsewhere (e.g. PositionTracker) and read from cache.
        pos_qty: float | None = 0.0
        # Position determination strategy:
        # 1. Prefer a fast snapshot from PositionTracker when available.
        # 2. If the snapshot is missing the symbol or is stale (TTL), consult
        #    the broker as the authoritative source via a thread call.
        # 3. If no tracker is present, call the broker directly.
        ttl_seconds = int(self.config.get("position_tracker_ttl_seconds", 5))
        now_ts = datetime.now(timezone.utc)

        async def _fetch_positions_from_broker() -> float | None:
            try:
                positions = await asyncio.to_thread(self.broker.get_positions)
                for p in positions:
                    if p.get("symbol") == symbol:
                        return float(p.get("qty", 0) or 0)
                return 0.0
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception(
                    "Failed to fetch positions from broker; treating position as unknown for %s",
                    symbol,
                )
                return None

        if self.position_tracker is not None:
            try:
                # PositionTracker.get_position is synchronous and returns PositionData or None
                pos_obj = self.position_tracker.get_position(symbol)
                if pos_obj is None:
                    # Unknown to tracker — consult broker (authoritative)
                    pos_qty = await _fetch_positions_from_broker()
                else:
                    # Use tracker value but check freshness
                    pos_qty = float(pos_obj.qty)
                    try:
                        last = self.position_tracker.last_updated()
                        if last is None or (now_ts - last).total_seconds() > ttl_seconds:
                            # Stale snapshot — reconcile with broker
                            broker_qty = await _fetch_positions_from_broker()
                            # If broker query succeeded, prefer authoritative value
                            if broker_qty is not None:
                                pos_qty = broker_qty
                    except Exception:
                        logger.exception(
                            "Failed to check PositionTracker freshness; using tracker value for %s",
                            symbol,
                        )
            except Exception:
                logger.exception(
                    "PositionTracker lookup failed; treating position as unknown for %s",
                    symbol,
                )
                pos_qty = await _fetch_positions_from_broker()
        else:
            # No tracker available — authoritative broker call
            pos_qty = await _fetch_positions_from_broker()

        # Decide action: buy -> ENTER_LONG (typically exposure-increasing)
        # sell -> if currently long, it's an exit; if flat, treat as ENTER_SHORT
        if side == "buy":
            action = "ENTER_LONG"
        else:
            # sell
            # If we couldn't determine current position (pos_qty is None),
            # be conservative and treat sell as an exit rather than opening a
            # short position.
            if pos_qty is None:
                action = "EXIT_LONG"
            elif pos_qty > 0:
                action = "EXIT_LONG"
            else:
                action = "ENTER_SHORT"

        # Log signal metadata (Win #2: multi-timeframe SMA + regime detection)
        sma_period = signal.metadata.get("sma_period", (10, 30))
        confidence = signal.metadata.get("confidence", 0.5)
        regime = signal.metadata.get("regime", "unknown")

        logger.info(
            f"Trading {symbol}: {side.upper()} "
            f"SMA{sma_period} confidence={confidence:.2f} regime={regime}"
        )

        # If this is an exposure-increasing action, enforce gating and pending checks
        entry_actions = ("ENTER_LONG", "ENTER_SHORT")
        if action in entry_actions:
            # Position-aware block: don't open same-direction exposure if already in position
            if action == "ENTER_LONG" and pos_qty is not None and pos_qty > 0:
                logger.info(
                    "Blocking entry: already_in_position symbol=%s action=%s pos_qty=%s",
                    symbol,
                    action,
                    pos_qty,
                )
                return False
            if action == "ENTER_SHORT" and pos_qty is not None and pos_qty < 0:
                logger.info(
                    "Blocking entry: already_in_position symbol=%s action=%s pos_qty=%s",
                    symbol,
                    action,
                    pos_qty,
                )
                return False

            # Pending-order-aware block
            entry_side = "buy" if action == "ENTER_LONG" else "sell"
            if self.state_store.has_open_exposure_increasing_order(
                symbol, entry_side, strategy=self.strategy_name
            ):
                logger.info(
                    "Blocking entry: open_order symbol=%s action=%s side=%s",
                    symbol,
                    action,
                    entry_side,
                )
                return False

            # Cooldown + per-bar dedupe via gate_try_accept
            cooldown_min = int(self.config.get("entry_cooldown_minutes", 120))
            from datetime import timedelta

            now_utc = datetime.now(timezone.utc)
            bar_ts = getattr(signal, "timestamp", None)
            cooldown_td = timedelta(minutes=cooldown_min)
            accepted = self.state_store.gate_try_accept(
                strategy=self.strategy_name,
                symbol=symbol,
                action=action,
                now_utc=now_utc,
                bar_ts_utc=bar_ts,
                cooldown=cooldown_td,
            )
            if not accepted:
                # Determine reason heuristically by re-checking recent state
                logger.info(
                    "Blocking entry: gate_rejected symbol=%s action=%s bar_ts=%s now_ts=%s cooldown_min=%s",
                    symbol,
                    action,
                    str(bar_ts),
                    now_utc.isoformat(),
                    cooldown_min,
                )
                return False

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
        metadata = getattr(signal, "metadata", {}) or {}
        atr_raw = metadata.get("atr") if isinstance(metadata, dict) else None
        atr_value: float | None = parse_optional_float(atr_raw)
        self.state_store.save_order_intent(
            client_order_id=client_order_id,
            strategy=self.strategy_name,
            symbol=symbol,
            side=side,
            qty=float(qty),  # Convert Decimal to float for storage
            atr=atr_value,
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
