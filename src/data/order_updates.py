"""Order update normalisation and persistence.

Receives raw order updates from Stream via DataHandler.
Normalises to OrderUpdateEvent.
Updates order_intents table.
If filled: inserts into trades table.
Publishes to EventBus.
"""

import logging
from datetime import datetime, timezone
from typing import Any

from src.event_bus import EventBus, OrderUpdateEvent
from src.state_store import StateStore

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
            # Normalise to OrderUpdateEvent
            event = self._normalise_order_update(raw_update)

            # Update order_intents table
            self.state_store.update_order_intent(
                client_order_id=event.client_order_id,
                status=event.status,
                filled_qty=event.filled_qty,
                alpaca_order_id=event.order_id,
            )

            # If filled: record in trades table
            if event.status == "filled":
                self._record_trade(event)

            # Publish to EventBus
            await self.event_bus.publish(event)

            logger.debug(f"Order update: {event.client_order_id} → {event.status}")
        except (AttributeError, TypeError, ValueError) as e:
            logger.error(f"Failed to process order update: {e}")

    def _normalise_order_update(self, raw_update: Any) -> OrderUpdateEvent:
        """Normalise raw SDK order update to OrderUpdateEvent."""
        # Safely access side attribute using getattr
        order = getattr(raw_update, "order", None)
        side_attr = getattr(order, "side", None)
        # Handle both enum (has .value) and string cases, lowercase result
        if side_attr is None:
            side_value = "unknown"
        elif hasattr(side_attr, "value"):
            side_value = str(getattr(side_attr, "value")).lower()
        else:
            side_value = str(side_attr).lower()

        # Safely access status with enum/string handling
        status_attr = getattr(order, "status", None)
        if status_attr is None:
            status_value = "unknown"
        elif hasattr(status_attr, "value"):
            status_value = str(getattr(status_attr, "value")).lower()
        else:
            status_value = str(status_attr).lower()

        # Safely access other order attributes with defaults
        order_id = getattr(order, "id", "") or ""
        client_order_id = getattr(order, "client_order_id", "") or ""
        symbol = getattr(order, "symbol", "") or ""
        filled_qty = getattr(order, "filled_qty", None)
        filled_avg_price = getattr(order, "filled_avg_price", None)

        return OrderUpdateEvent(
            order_id=order_id,
            client_order_id=client_order_id,
            symbol=symbol,
            side=side_value,
            status=status_value,
            filled_qty=float(filled_qty) if filled_qty else 0,
            avg_fill_price=float(filled_avg_price) if filled_avg_price else None,
            timestamp=raw_update.at if raw_update.at else datetime.now(timezone.utc),
        )

    def _record_trade(self, event: OrderUpdateEvent) -> None:
        """Record filled order in trades table."""
        import sqlite3

        if event.avg_fill_price is None:
            logger.warning(f"Filled order {event.client_order_id} has no fill price")
            return

        conn = sqlite3.connect(self.state_store.db_path)
        cursor = conn.cursor()

        cursor.execute(
            """INSERT INTO trades 
               (timestamp_utc, symbol, side, qty, price, order_id, client_order_id)
               VALUES (?, ?, ?, ?, ?, ?, ?)""",
            (
                event.timestamp.isoformat(),
                event.symbol,
                event.side,
                event.filled_qty,
                event.avg_fill_price,
                event.order_id,
                event.client_order_id,
            ),
        )
        conn.commit()
        conn.close()
