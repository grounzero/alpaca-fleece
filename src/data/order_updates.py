"""Order update processing and persistence.

Receives raw order updates from the stream via the DataHandler.
Converts them to OrderUpdateEvent objects.
Computes incremental (delta) fills from cumulative broker data.
Persists fill rows idempotently in the fills table.
Updates the order_intents table with monotonically increasing cumulative fills.
If terminal (filled), records in the trades table (idempotent).
Publishes OrderUpdateEvent (with delta_qty) to the EventBus.
"""

import logging
from datetime import datetime, timezone
from typing import Any, Optional

from src.event_bus import EventBus, OrderUpdateEvent
from src.models.order_state import OrderState
from src.position_tracker import PositionTracker
from src.state_store import StateStore
from src.utils import parse_optional_float

logger = logging.getLogger(__name__)

# Epsilon for floating-point comparisons on fill quantities
_QTY_EPSILON = 1e-9


class OrderUpdatesHandler:
    """Order updates handler with partial-fill support."""

    def __init__(
        self,
        state_store: StateStore,
        event_bus: EventBus,
        position_tracker: Optional[PositionTracker] = None,
    ) -> None:
        """Initialise order updates handler.

        Args:
            state_store: SQLite state store
            event_bus: Event bus for publishing
            position_tracker: Optional PositionTracker for fill-to-position wiring
        """
        self.state_store = state_store
        self.event_bus = event_bus
        self.position_tracker = position_tracker

    async def on_order_update(self, raw_update: Any) -> None:
        """Process raw order update from stream.

        Computes incremental fills, persists them idempotently, and publishes
        an OrderUpdateEvent with the computed delta_qty.

        Args:
            raw_update: Raw order update object from SDK
        """
        try:
            # Convert raw SDK update to a canonical OrderUpdateEvent (without delta yet)
            event = self._to_canonical_order_update(raw_update)

            # Compute delta fill and get the augmented event
            event = self._process_fill_delta(event)

            # Terminal trade recording (idempotent)
            if event.state == OrderState.FILLED:
                self._record_trade(event)

            # Update PositionTracker on fill delta (if tracker available)
            # Use the per-delta fill price if available; fall back to cumulative
            # average fill price for backward compatibility.
            delta_fill_price = getattr(event, "delta_fill_price", None)
            if delta_fill_price is None:
                delta_fill_price = event.cum_avg_fill_price

            if (
                self.position_tracker is not None
                and event.delta_qty is not None
                and event.delta_qty > 0
                and delta_fill_price is not None
            ):
                await self.position_tracker.update_position_from_fill(
                    symbol=event.symbol,
                    delta_qty=event.delta_qty,
                    fill_price=delta_fill_price,
                    side=event.side,
                    timestamp=event.timestamp,
                )

            # Publish to EventBus
            await self.event_bus.publish(event)

            if event.delta_qty and event.delta_qty > 0:
                logger.info(
                    "Fill delta: order=%s symbol=%s delta_qty=%.4f cum_qty=%.4f status=%s",
                    event.client_order_id,
                    event.symbol,
                    event.delta_qty,
                    event.cum_filled_qty,
                    event.status,
                )
            else:
                logger.debug(
                    "Order update: %s → %s (no new fill)",
                    event.client_order_id,
                    event.status,
                )
        except (AttributeError, TypeError, ValueError) as e:
            logger.error(f"Failed to process order update: {e}")

    def _process_fill_delta(self, event: OrderUpdateEvent) -> OrderUpdateEvent:
        """Compute delta fill, persist fill row, and update order_intents.

        Returns a new OrderUpdateEvent with delta_qty set.

        Requires alpaca_order_id to be non-null for fill processing. If missing,
        returns event with delta_qty=0 and logs a warning.
        """
        alpaca_order_id = event.order_id
        client_order_id = event.client_order_id
        timestamp_utc = event.timestamp.isoformat()

        # Guard: alpaca_order_id is required for all fill operations
        if not alpaca_order_id:
            logger.warning(
                "Cannot process fills: missing alpaca_order_id for order %s",
                client_order_id,
            )
            return OrderUpdateEvent(
                order_id=event.order_id,
                client_order_id=event.client_order_id,
                symbol=event.symbol,
                side=event.side,
                status=event.status,
                state=event.state,
                filled_qty=event.filled_qty,
                avg_fill_price=event.avg_fill_price,
                fill_id=event.fill_id,
                timestamp=event.timestamp,
                delta_qty=0.0,
            )

        # Resolve the order intent to get prev_cum_qty
        prev_cum_qty = 0.0
        if alpaca_order_id and self.state_store is not None:
            prev_cum_qty = self.state_store.get_last_cum_qty_for_order(alpaca_order_id)

        # Parse new cumulative qty, guard against None/NaN
        new_cum_qty = event.cum_filled_qty if event.cum_filled_qty is not None else prev_cum_qty

        # Monotonic regression guard
        if new_cum_qty < prev_cum_qty - _QTY_EPSILON:
            logger.warning(
                "Out-of-order cum fill regression: order=%s prev=%.4f new=%.4f — "
                "ignoring fill decrease",
                client_order_id,
                prev_cum_qty,
                new_cum_qty,
            )
            # Still update status/timestamp but don't apply fill
            if alpaca_order_id and self.state_store is not None:
                self.state_store.update_order_intent_cumulative(
                    alpaca_order_id=alpaca_order_id,
                    status=event.status,
                    new_cum_qty=prev_cum_qty,  # keep old value
                    new_cum_avg_price=event.cum_avg_fill_price,
                    timestamp_utc=timestamp_utc,
                )
            return OrderUpdateEvent(
                order_id=event.order_id,
                client_order_id=event.client_order_id,
                symbol=event.symbol,
                side=event.side,
                status=event.status,
                state=event.state,
                filled_qty=event.filled_qty,
                avg_fill_price=event.avg_fill_price,
                fill_id=event.fill_id,
                timestamp=event.timestamp,
                delta_qty=0.0,
            )

        delta_qty = max(0.0, new_cum_qty - prev_cum_qty)

        # Persist cumulative update to order_intents (monotonic)
        if self.state_store is not None:
            self.state_store.update_order_intent_cumulative(
                alpaca_order_id=alpaca_order_id,
                status=event.status,
                new_cum_qty=new_cum_qty,
                new_cum_avg_price=event.cum_avg_fill_price,
                timestamp_utc=timestamp_utc,
            )

        fill_inserted = False
        if delta_qty > _QTY_EPSILON and alpaca_order_id and self.state_store is not None:
            fill_inserted = self.state_store.insert_fill_idempotent(
                alpaca_order_id=alpaca_order_id,
                client_order_id=client_order_id,
                symbol=event.symbol,
                side=event.side,
                delta_qty=delta_qty,
                cum_qty=new_cum_qty,
                cum_avg_price=event.cum_avg_fill_price,
                timestamp_utc=timestamp_utc,
                fill_id=event.fill_id,
                price_is_estimate=True,
            )

            if fill_inserted:
                logger.info(
                    "Fill inserted: order=%s delta=%.4f cum=%.4f",
                    client_order_id,
                    delta_qty,
                    new_cum_qty,
                )
            else:
                logger.debug(
                    "Fill duplicate (idempotent skip): order=%s cum=%.4f",
                    client_order_id,
                    new_cum_qty,
                )
                # Duplicate fill — set delta to 0 so downstream doesn't double-count
                delta_qty = 0.0

        # Return augmented event with delta_qty
        return OrderUpdateEvent(
            order_id=event.order_id,
            client_order_id=event.client_order_id,
            symbol=event.symbol,
            side=event.side,
            status=event.status,
            state=event.state,
            filled_qty=event.filled_qty,
            avg_fill_price=event.avg_fill_price,
            fill_id=event.fill_id,
            timestamp=event.timestamp,
            delta_qty=delta_qty if delta_qty > _QTY_EPSILON else 0.0,
        )

    def _extract_enum_value(self, attr: Any, default: str = "unknown") -> str:
        """Convert enum-or-string attributes to a lowercase string with a default.

        Handles attributes that may be:
        - None
        - Enum-like objects with a `.value` attribute
        - Plain strings or other primitives
        """
        if attr is None:
            return default

        # Use `.value` if present (e.g., Enum), otherwise use the attribute itself.
        value = getattr(attr, "value", attr)
        return str(value).lower()

    def _to_canonical_order_update(self, raw_update: Any) -> OrderUpdateEvent:
        """Convert raw SDK order update to a canonical OrderUpdateEvent."""
        # Support both object-like and dict-like order representations.
        order = getattr(raw_update, "order", None)

        def _get_raw(name: str) -> Any:
            if order is None:
                return None
            # Prefer getattr for SDK objects
            val = getattr(order, name, None)
            # Fallback to dict-like access
            if val is None and isinstance(order, dict):
                val = order.get(name)
            return val

        # Safely access side and status using the unified helper so dict-shaped
        # orders are handled the same as SDK objects and don't silently drop
        # critical fields.
        side_attr = _get_raw("side")
        side_value = self._extract_enum_value(side_attr)

        status_attr = _get_raw("status")
        status_value = self._extract_enum_value(status_attr)

        # Use the same helper for identifiers/metadata so dict inputs don't
        # become blank strings.
        order_id = _get_raw("id") or ""
        client_order_id = _get_raw("client_order_id") or ""
        symbol = _get_raw("symbol") or ""

        raw_filled_qty = _get_raw("filled_qty")
        raw_filled_avg_price = _get_raw("filled_avg_price")

        parsed_filled_qty: Optional[float] = parse_optional_float(raw_filled_qty)
        parsed_filled_avg_price: Optional[float] = parse_optional_float(raw_filled_avg_price)

        # Attempt to extract a fill id if provided by the broker payload.
        # Some brokers include a `fill_id` or a `fills` list with ids.
        raw_fill_id = _get_raw("fill_id")
        if raw_fill_id is None:
            # Try `fills` list fallback
            raw_fills = _get_raw("fills")
            if isinstance(raw_fills, (list, tuple)) and len(raw_fills) > 0:
                first_fill = raw_fills[0]
                if isinstance(first_fill, dict):
                    raw_fill_id = first_fill.get("id") or first_fill.get("fill_id")
                else:
                    # object-like fill
                    raw_fill_id = getattr(first_fill, "id", None) or getattr(
                        first_fill, "fill_id", None
                    )

        fill_id_value: Optional[str] = None
        if raw_fill_id is not None:
            fill_id_value = str(raw_fill_id)

        # Convert status to OrderState enum
        order_state = OrderState.from_alpaca(status_value)

        return OrderUpdateEvent(
            order_id=order_id,
            client_order_id=client_order_id,
            symbol=symbol,
            side=side_value,
            status=status_value,
            state=order_state,
            filled_qty=parsed_filled_qty,
            avg_fill_price=parsed_filled_avg_price,
            fill_id=fill_id_value,
            timestamp=getattr(raw_update, "at", None) or datetime.now(timezone.utc),
        )

    def _record_trade(self, event: OrderUpdateEvent) -> None:
        """Record filled order in trades table (idempotent)."""
        import sqlite3

        if self.state_store is None:
            return

        # Require an avg fill price to record trades
        if event.avg_fill_price is None:
            logger.warning("Filled order %s has no fill price", event.client_order_id)
            return

        # If filled_qty is None, attempt to retrieve the last known value from
        # the StateStore (order_intents). If still missing, skip recording to
        # avoid inserting NULL/invalid quantities into the trades table.
        qty_to_record: Optional[float] = event.filled_qty
        if qty_to_record is None:
            oi = self.state_store.get_order_intent(event.client_order_id)
            if oi:
                qty_to_record = parse_optional_float(oi.get("filled_qty"))

        if qty_to_record is None:
            logger.warning(
                "Skipping trade record for %s: filled_qty is missing",
                event.client_order_id,
            )
            return

        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()

            sql = """
                INSERT INTO trades (timestamp_utc, symbol, side, qty, price, order_id, client_order_id, fill_id)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(order_id, client_order_id) DO UPDATE SET
                  fill_id = COALESCE(trades.fill_id, excluded.fill_id)
                """

            params = (
                event.timestamp.isoformat(),
                event.symbol,
                event.side,
                float(qty_to_record),
                float(event.avg_fill_price),
                event.order_id,
                event.client_order_id,
                getattr(event, "fill_id", None),
            )

            try:
                cursor.execute(sql, params)
            except sqlite3.IntegrityError:
                logger.exception(
                    "Failed to insert/update trade for %s due to integrity error",
                    event.client_order_id,
                )
                raise

    def record_filled_trade(self, event: OrderUpdateEvent) -> None:
        """Public wrapper to record a filled trade.

        This small wrapper exists so callers (and tests) can exercise the
        trade-recording logic without reaching into a private method or
        invoking the async `on_order_update` entrypoint.
        """
        self._record_trade(event)
