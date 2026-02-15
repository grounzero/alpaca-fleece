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
import sqlite3
import inspect
import logging
from datetime import datetime, timedelta, timezone
from typing import Any

from src.async_broker_adapter import (
    AsyncBrokerInterface,
    BrokerTimeoutError,
    BrokerTransientError,
    BrokerFatalError,
)
from src.broker import BrokerError
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
        broker: AsyncBrokerInterface,
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

        # Optionally enforce that a PositionTracker is provided. This is
        # controlled by the `REQUIRE_POSITION_TRACKER` flag in `config` so
        # unit tests and backtests can remain lightweight while orchestrator
        # can opt into strict wiring during runtime startup.
        require_tracker = bool(self.config.get("REQUIRE_POSITION_TRACKER", False))
        if require_tracker and self.position_tracker is None:
            raise ValueError(
                "OrderManager requires a PositionTracker instance when REQUIRE_POSITION_TRACKER is set"
            )

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

    def _generate_shutdown_client_order_id(
        self, symbol: str, shutdown_session_id: str, action: str
    ) -> str:
        """Deterministic client_order_id for shutdown flatten actions.

        Args:
            symbol: Stock symbol
            shutdown_session_id: unique shutdown session identifier
            action: e.g. 'flatten'

        Returns:
            16-char hex string
        """
        data = f"shutdown:{self.strategy_name}:{symbol}:{shutdown_session_id}:{action}"
        return hashlib.sha256(data.encode()).hexdigest()[:16]

    def _coerce_qty(self, raw_qty: Any) -> float:
        """Coerce various broker qty representations to float safely.

        Handles numeric types, numeric strings, and returns 0.0 for
        None/unparseable values. Mirrors the previous ad-hoc logic used
        in `flatten_positions` to ensure consistent behaviour.
        """
        if raw_qty is None:
            return 0.0
        try:
            return float(raw_qty)
        except (ValueError, TypeError):
            return 0.0

    async def flatten_positions(self, shutdown_session_id: str) -> dict[str, Any]:
        """Flatten all current broker positions as part of shutdown.

        This method is resilient: it attempts to submit a reduce-only intent
        for each symbol and continues on per-symbol failure.

        Args:
            shutdown_session_id: A caller-supplied identifier that is unique per
                shutdown attempt for this strategy. It is used to derive
                deterministic, idempotent client order IDs via
                :meth:`_generate_shutdown_client_order_id`. Reusing the same
                value for multiple shutdown attempts will reuse the same
                client order IDs at the broker; this may or may not be
                acceptable depending on broker semantics, so callers should
                generally provide a fresh, unique value (e.g. a UUID or
                timestamp-based string) for each shutdown sequence.

        Returns:
            dict: A summary of the shutdown-flatten attempt with the keys:

                - ``submitted``: list of per-symbol submissions that were
                  successfully handed off to the broker/state store. Each
                  element is a dict that includes at least ``symbol`` and
                  ``client_order_id`` (and may contain other fields populated
                  by the underlying submission logic).

                - ``failed``: list of per-symbol failures that occurred while
                  attempting to submit flatten orders. Each element is a dict
                  of the form ``{"symbol": <symbol>, "error": <string>}``
                  describing the failure for that symbol.

                - ``remaining_exposure_symbols``: list of symbols for which
                  the system still has non-flat broker positions after this
                  method completes. This is computed by re-querying positions
                  at the end of the routine.

        Raises:
            OrderManagerError: If broker positions cannot be queried at the
                start of the shutdown sequence. Per-symbol submission errors
                do *not* cause this method to raise; instead they are
                accumulated in the ``failed`` list in the returned dict.

        Notes:
            - This coroutine is intended to be invoked as part of a single
              orderly shutdown flow running in one event loop. It does not
              implement additional locking and should not be called
              concurrently for the same strategy, especially with the same
              ``shutdown_session_id``, as that could lead to duplicate or
              conflicting broker submissions.
            - It is safe for the method to continue processing other symbols
              even if individual submissions fail; such failures are captured
              in the result summary.
        """
        results: dict[str, Any] = {
            "submitted": [],
            "failed": [],
            "remaining_exposure_symbols": [],
        }

        # Fetch authoritative positions from broker (async-safe)
        try:
            maybe_positions = self.broker.get_positions()
            positions = (
                await maybe_positions if inspect.isawaitable(maybe_positions) else maybe_positions
            )
        except (
            ConnectionError,
            TimeoutError,
            BrokerError,
            BrokerTimeoutError,
            BrokerTransientError,
            BrokerFatalError,
        ) as e:
            logger.exception("Failed to query broker positions during shutdown: %s", e)
            # Cannot proceed reliably without positions
            raise OrderManagerError("Failed to query positions for shutdown")

        for p in positions:
            try:
                symbol = p.get("symbol")
                if not symbol:
                    logger.warning("Skipping malformed position without symbol: %s", p)
                    results["failed"].append({"symbol": None, "error": "missing_symbol"})
                    continue
                raw_qty = p.get("qty")
                # Broker may return qty as string - coerce safely
                qty_val = self._coerce_qty(raw_qty)

                if qty_val == 0:
                    logger.info("Skipping position with zero quantity during shutdown: %s", p)
                    continue

                side = "sell" if qty_val > 0 else "buy"
                abs_qty = abs(qty_val)

                client_order_id = self._generate_shutdown_client_order_id(
                    symbol=symbol, shutdown_session_id=shutdown_session_id, action="flatten"
                )

                # Duplicate prevention: if an intent exists, skip re-submit
                if self.state_store.get_order_intent(client_order_id):
                    logger.info("Shutdown duplicate prevented: %s", client_order_id)
                    results["submitted"].append(
                        {"symbol": symbol, "client_order_id": client_order_id, "skipped": True}
                    )
                    continue

                # Persist shutdown intent BEFORE submitting. Handle race where
                # another concurrent caller may have inserted the same
                # client_order_id between the existence check and this insert.
                try:
                    self.state_store.save_order_intent(
                        client_order_id=client_order_id,
                        strategy="shutdown",
                        symbol=symbol,
                        side=side,
                        qty=float(abs_qty),
                        atr=None,
                        status="new",
                    )
                except sqlite3.IntegrityError:
                    # Another worker inserted the same client_order_id concurrently.
                    logger.info("Shutdown duplicate prevented (race): %s", client_order_id)
                    results["submitted"].append(
                        {"symbol": symbol, "client_order_id": client_order_id, "skipped": True}
                    )
                    continue
                except Exception as e:
                    logger.exception("Failed to persist shutdown intent %s: %s", client_order_id, e)
                    results["failed"].append({"symbol": symbol, "error": f"persist_error: {e}"})
                    try:
                        metric_inc = getattr(self.broker, "_metric_inc", None)
                        if callable(metric_inc):
                            await metric_inc("shutdown_flatten_submit_failed_total")
                        else:
                            metrics = getattr(self.broker, "metrics", None)
                            if isinstance(metrics, dict):
                                metrics["shutdown_flatten_submit_failed_total"] = (
                                    metrics.get("shutdown_flatten_submit_failed_total", 0) + 1
                                )
                    except Exception:
                        pass
                    continue

                # Submit to broker. Use market order to reduce exposure; ensure
                # side is opposite of the position so it is reduce-only in intent.
                maybe = self.broker.submit_order(
                    symbol=symbol,
                    side=side,
                    qty=float(abs_qty),
                    client_order_id=client_order_id,
                    order_type=self.order_type,
                    time_in_force=self.time_in_force,
                )
                order = await maybe if inspect.isawaitable(maybe) else maybe

                # Update persisted intent with broker id
                self.state_store.update_order_intent(
                    client_order_id=client_order_id,
                    status="submitted",
                    filled_qty=None,
                    alpaca_order_id=order.get("id"),
                )

                # Invalidate caches if adapter supports it
                try:
                    if hasattr(self.broker, "invalidate_cache"):
                        maybe_inv = self.broker.invalidate_cache("get_positions", "get_open_orders")
                        if inspect.isawaitable(maybe_inv):
                            await maybe_inv
                except Exception:
                    logger.debug("invalidate_cache failed after shutdown submit", exc_info=True)

                logger.info(
                    "shutdown_action=flatten symbol=%s side=%s qty=%s client_order_id=%s outcome=submitted",
                    symbol,
                    side,
                    abs_qty,
                    client_order_id,
                )
                # metrics (async-safe increment if broker provides helper)
                try:
                    metric_inc = getattr(self.broker, "_metric_inc", None)
                    if callable(metric_inc):
                        try:
                            res = metric_inc("shutdown_flatten_submit_total")
                            if inspect.isawaitable(res):
                                await res
                        except Exception:
                            # If the helper itself raises synchronously, fall through
                            # to the fallback metrics dict below.
                            raise
                    else:
                        metrics = getattr(self.broker, "metrics", None)
                        if isinstance(metrics, dict):
                            metrics["shutdown_flatten_submit_total"] = (
                                metrics.get("shutdown_flatten_submit_total", 0) + 1
                            )
                except Exception:
                    # Best-effort metrics; never propagate failures from metrics
                    try:
                        metrics = getattr(self.broker, "metrics", None)
                        if isinstance(metrics, dict):
                            metrics["shutdown_flatten_submit_total"] = (
                                metrics.get("shutdown_flatten_submit_total", 0) + 1
                            )
                    except Exception:
                        pass

                results["submitted"].append({"symbol": symbol, "client_order_id": client_order_id})
            except Exception as e:
                logger.exception("Failed to flatten symbol %s: %s", p.get("symbol"), e)
                results["failed"].append({"symbol": p.get("symbol"), "error": str(e)})
                try:
                    metric_inc = getattr(self.broker, "_metric_inc", None)
                    if callable(metric_inc):
                        try:
                            res = metric_inc("shutdown_flatten_submit_failed_total")
                            if inspect.isawaitable(res):
                                await res
                        except Exception:
                            raise
                    else:
                        metrics = getattr(self.broker, "metrics", None)
                        if isinstance(metrics, dict):
                            metrics["shutdown_flatten_submit_failed_total"] = (
                                metrics.get("shutdown_flatten_submit_failed_total", 0) + 1
                            )
                except Exception:
                    try:
                        metrics = getattr(self.broker, "metrics", None)
                        if isinstance(metrics, dict):
                            metrics["shutdown_flatten_submit_failed_total"] = (
                                metrics.get("shutdown_flatten_submit_failed_total", 0) + 1
                            )
                    except Exception:
                        pass
                # continue with other symbols
        # Re-query positions to determine remaining exposure
        try:
            maybe_positions2 = self.broker.get_positions()
            positions2 = (
                await maybe_positions2
                if inspect.isawaitable(maybe_positions2)
                else maybe_positions2
            )
            for p in positions2:
                q = self._coerce_qty(p.get("qty"))
                if q != 0:
                    results["remaining_exposure_symbols"].append(p.get("symbol"))
        except Exception:
            # If re-query fails, we can't determine remaining exposure reliably
            logger.warning(
                "Failed to re-query positions after shutdown flatten; remaining exposure unknown"
            )

        # Emit remaining metric (prefer an async-safe update when available)
        try:
            metric_inc = getattr(self.broker, "_metric_inc", None)
            metrics = getattr(self.broker, "metrics", None)
            # Prefer calling the metric helper if provided. It may be
            # sync or async; handle both cases. If it isn't provided,
            # fall back to updating a dict-shaped `metrics` object.
            if callable(metric_inc):
                try:
                    res = metric_inc(
                        "shutdown_remaining_exposure_symbols",
                        len(results["remaining_exposure_symbols"]),
                    )
                    if inspect.isawaitable(res):
                        await res
                except Exception:
                    # If helper fails, fall back to metrics dict below
                    raise
            elif isinstance(metrics, dict):
                lock = getattr(self.broker, "_cache_lock", None)
                if lock is not None:
                    async with lock:
                        metrics["shutdown_remaining_exposure_symbols"] = len(
                            results["remaining_exposure_symbols"]
                        )
                else:
                    metrics["shutdown_remaining_exposure_symbols"] = len(
                        results["remaining_exposure_symbols"]
                    )
        except Exception:
            try:
                metrics = getattr(self.broker, "metrics", None)
                if isinstance(metrics, dict):
                    metrics["shutdown_remaining_exposure_symbols"] = len(
                        results["remaining_exposure_symbols"]
                    )
            except Exception:
                pass

        return results

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
        # NOTE: `submit_order` is async. Position lookup avoids blocking the event loop
        # by preferring cached data from PositionTracker and otherwise calling
        # `Broker.get_positions` via `await asyncio.to_thread(self.broker.get_positions)`.
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
                positions = await self.broker.get_positions()
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
                        last = self.position_tracker.last_updated(symbol)
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
            # If we couldn't determine current position (pos_qty is None),
            # assume this is an entry. If we're currently short (pos_qty < 0),
            # treat BUY as a cover/exit rather than opening a long.
            if pos_qty is None:
                action = "ENTER_LONG"
            elif pos_qty < 0:
                action = "EXIT_SHORT"
            else:
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

            # Copilot AI: Importing `timedelta` inside `submit_order()` adds avoidable
            # overhead on a hot path and is inconsistent with the module's other
            # imports. `timedelta` is imported at module level to keep imports
            # centralized and reduce per-call work.
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
            maybe: Any = self.broker.submit_order(
                symbol=symbol,
                side=side,
                qty=float(qty),  # Convert Decimal to float for API
                client_order_id=client_order_id,
                order_type=self.order_type,
                time_in_force=self.time_in_force,
            )
            order = await maybe if inspect.isawaitable(maybe) else maybe

            # Update with Alpaca order ID. Do not overwrite filled fields; pass
            # None so StateStore's COALESCE preserves existing values when
            # the broker response omits them.
            self.state_store.update_order_intent(
                client_order_id=client_order_id,
                status="submitted",
                filled_qty=None,
                alpaca_order_id=order.get("id"),
            )

            # Invalidate adapter cache for write consistency so subsequent
            # reads (positions / open orders) observe the change sooner.
            try:
                if hasattr(self.broker, "invalidate_cache"):
                    maybe = self.broker.invalidate_cache("get_positions", "get_open_orders")
                    if inspect.isawaitable(maybe):
                        await maybe
            except Exception as e:
                # Re-raise common developer exceptions so they surface in test runs
                # while still logging transient failures.
                if isinstance(e, (TypeError, AttributeError, NameError)):
                    raise
                logger.exception("Failed to invalidate broker cache after submit")

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
