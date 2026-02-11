"""Order update processing and persistence.

Receives raw order updates from the stream via the DataHandler.
Converts them to OrderUpdateEvent objects.
Updates the order_intents table.
If filled, inserts a record into the trades table.
Publishes to EventBus.
"""

import logging
from datetime import datetime, timezone
from typing import Any, Optional

from src.event_bus import EventBus, OrderUpdateEvent
from src.state_store import StateStore
from src.utils import parse_optional_float

logger = logging.getLogger(__name__)


class OrderUpdatesHandler:
    """Order updates handler."""

    def __init__(self, state_store: StateStore, event_bus: EventBus) -> None:
        """Initialise order updates handler.

        Args:
            state_store: SQLite state store
            event_bus: Event bus for publishing
        """
        self.state_store = state_store
        self.event_bus = event_bus

    async def on_order_update(self, raw_update: Any) -> None:
        """Process raw order update from stream.

        Args:
            raw_update: Raw order update object from SDK
        """
        try:
            # Convert raw SDK update to a canonical OrderUpdateEvent
            event = self._to_canonical_order_update(raw_update)

            # Update order_intents table (including avg fill price when available)
            self.state_store.update_order_intent(
                client_order_id=event.client_order_id,
                status=event.status,
                filled_qty=event.filled_qty,
                alpaca_order_id=event.order_id,
                filled_avg_price=event.avg_fill_price,
            )

            # If filled: record in trades table
            if event.status == "filled":
                self._record_trade(event)

            # Publish to EventBus
            await self.event_bus.publish(event)

            logger.debug(f"Order update: {event.client_order_id} → {event.status}")
        except (AttributeError, TypeError, ValueError) as e:
            logger.error(f"Failed to process order update: {e}")

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
        # Safely access side attribute using getattr
        order = getattr(raw_update, "order", None)
        side_attr = getattr(order, "side", None)
        side_value = self._extract_enum_value(side_attr)

        # Safely access status with enum/string handling
        status_attr = getattr(order, "status", None)
        status_value = self._extract_enum_value(status_attr)

        # Safely access other order attributes with defaults
        order_id = getattr(order, "id", "") or ""
        client_order_id = getattr(order, "client_order_id", "") or ""
        symbol = getattr(order, "symbol", "") or ""

        # Support both object-like and dict-like order representations.
        def _get_raw(name: str) -> Any:
            if order is None:
                return None
            # Prefer getattr for SDK objects
            val = getattr(order, name, None)
            # Fallback to dict-like access
            if val is None and isinstance(order, dict):
                val = order.get(name)
            return val

        raw_filled_qty = _get_raw("filled_qty")
        raw_filled_avg_price = _get_raw("filled_avg_price")

        parsed_filled_qty: Optional[float] = parse_optional_float(raw_filled_qty)
        parsed_filled_avg_price: Optional[float] = parse_optional_float(raw_filled_avg_price)

        # Note: `parsed_filled_qty` and `parsed_filled_avg_price` may be None.
        # We intentionally preserve None here (do not coerce filled_qty to 0)
        # so that the StateStore's SQL `COALESCE(?, filled_qty)` can decide
        # whether to overwrite the stored value. Downstream consumers that
        # assume `filled_qty` is numeric must guard against None (for example,
        # `_record_trade` should skip recording a trade or retrieve the
        # existing DB quantity when `filled_qty` is None) to avoid inserting
        # invalid trades or violating DB constraints.
        return OrderUpdateEvent(
            order_id=order_id,
            client_order_id=client_order_id,
            symbol=symbol,
            side=side_value,
            status=status_value,
            filled_qty=parsed_filled_qty,
            avg_fill_price=parsed_filled_avg_price,
            timestamp=getattr(raw_update, "at", None) or datetime.now(timezone.utc),
        )

    def _record_trade(self, event: OrderUpdateEvent) -> None:
        """Record filled order in trades table."""
        import sqlite3

        # Require an avg fill price to record trades
        if event.avg_fill_price is None:
            logger.warning(f"Filled order {event.client_order_id} has no fill price")
            return

        # If filled_qty is None, attempt to retrieve the last known value from
        # the StateStore (order_intents). If still missing, skip recording to
        # avoid inserting NULL/invalid quantities into the trades table.
        qty_to_record = event.filled_qty
        if qty_to_record is None:
            oi = self.state_store.get_order_intent(event.client_order_id)
            if oi:
                qty_to_record = oi.get("filled_qty")

        if qty_to_record is None:
            logger.warning(
                "Skipping trade record for %s: filled_qty is missing",
                event.client_order_id,
            )
            return

        # Insert trade using a context manager for the DB connection
        with sqlite3.connect(self.state_store.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute(
                """INSERT INTO trades 
                   (timestamp_utc, symbol, side, qty, price, order_id, client_order_id)
                   VALUES (?, ?, ?, ?, ?, ?, ?)""",
                (
                    event.timestamp.isoformat(),
                    event.symbol,
                    event.side,
                    float(qty_to_record),
                    float(event.avg_fill_price),
                    event.order_id,
                    event.client_order_id,
                ),
            )
            conn.commit()
