"""Order update normalisation and persistence.

Receives raw order updates from Stream via DataHandler.
Normalises to OrderUpdateEvent.
Updates order_intents table.
If filled: inserts into trades table.
Publishes to EventBus.
"""

import logging
from datetime import datetime, timezone

from src.event_bus import OrderUpdateEvent, EventBus
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

    async def on_order_update(self, raw_update) -> None:
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

    def _normalise_order_update(self, raw_update) -> OrderUpdateEvent:
        """Normalise raw SDK order update to OrderUpdateEvent."""
        return OrderUpdateEvent(
            order_id=raw_update.order.id,
            client_order_id=raw_update.order.client_order_id,
            symbol=raw_update.order.symbol,
            status=raw_update.order.status.value if raw_update.order.status else "unknown",
            filled_qty=float(raw_update.order.filled_qty) if raw_update.order.filled_qty else 0,
            avg_fill_price=(
                float(raw_update.order.filled_avg_price)
                if raw_update.order.filled_avg_price
                else None
            ),
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
                "buy",  # TODO: extract from event or order details
                event.filled_qty,
                event.avg_fill_price,
                event.order_id,
                event.client_order_id,
            ),
        )
        conn.commit()
        conn.close()
