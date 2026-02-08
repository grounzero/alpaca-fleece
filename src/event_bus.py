"""Event bus - asyncio.Queue for event flow.

Single queue with publish/subscribe pattern.
All internal events flow through here.
"""

import asyncio
import logging
from dataclasses import dataclass
from datetime import datetime
from typing import Optional
<<<<<<< HEAD


# Event types
@dataclass(frozen=True)
class BarEvent:
    """Market bar received and persisted."""

=======

logger = logging.getLogger(__name__)


class EventBusError(Exception):
    """Raised when event bus operation fails."""

    pass


# Event types
@dataclass(frozen=True)
class BarEvent:
    """Market bar received and persisted."""

>>>>>>> 7e787d8 (Clean trading bot implementation)
    symbol: str
    timestamp: datetime
    open: float
    high: float
    low: float
    close: float
    volume: int
    trade_count: Optional[int] = None
    vwap: Optional[float] = None


@dataclass(frozen=True)
class SignalEvent:
    """Trading signal from strategy."""

    symbol: str
    signal_type: str  # "BUY", "SELL"
    timestamp: datetime
    metadata: dict[str, object]


@dataclass(frozen=True)
class OrderIntentEvent:
    """Order intent before submission."""

    symbol: str
    side: str  # "buy", "sell"
    qty: float
    client_order_id: str
    timestamp: datetime


@dataclass(frozen=True)
class OrderUpdateEvent:
    """Order status update from broker."""

    order_id: str
    client_order_id: str
    symbol: str
    status: str  # new, filled, partially_filled, canceled, rejected, expired
    filled_qty: float
    avg_fill_price: Optional[float]
    timestamp: datetime


@dataclass(frozen=True)
class ExitSignalEvent:
    """Exit signal for position management.

    Published when exit threshold is breached (stop loss, profit target, trailing stop).
    """

    symbol: str
    side: str  # "sell" for long positions, "buy" for short positions
    qty: float
    reason: str  # "stop_loss", "trailing_stop", "profit_target", "emergency", "circuit_breaker"
    entry_price: float
    current_price: float
    pnl_pct: float
    pnl_amount: float
    timestamp: datetime


class EventBus:
    """Async event bus using asyncio.Queue."""

    def __init__(self, maxsize: int = 10000) -> None:
        """Initialise event bus.

        Args:
            maxsize: Max items in queue
        """
        self.queue: asyncio.Queue[object] = asyncio.Queue(maxsize=maxsize)
        self.running = False
<<<<<<< HEAD
=======
        self._dropped_count = 0
>>>>>>> 7e787d8 (Clean trading bot implementation)

    async def publish(self, event: object) -> None:
        """Publish event to queue.

        Args:
            event: Event object (BarEvent, SignalEvent, etc)
<<<<<<< HEAD
        """
        if not self.running:
            return

        try:
            await asyncio.wait_for(self.queue.put(event), timeout=5.0)
        except asyncio.TimeoutError:
            # Queue full, skip (log elsewhere)
            pass

    async def subscribe(self) -> Optional[object]:
        """Get next event from queue (blocking).

        Returns:
            Event object or None if bus stopped
        """
        if not self.running:
            return None

        try:
            event = await asyncio.wait_for(self.queue.get(), timeout=1.0)
            return event
        except asyncio.TimeoutError:
            return None

=======

        Raises:
            EventBusError: If critical event (ExitSignalEvent) cannot be published
        """
        if not self.running:
            return

        try:
            await asyncio.wait_for(self.queue.put(event), timeout=5.0)
        except asyncio.TimeoutError:
            self._dropped_count += 1
            event_type = type(event).__name__
            logger.error(
                f"EventBus queue full - DROPPED event: type={event_type}, "
                f"total_dropped={self._dropped_count}",
                extra={"event_type": event_type, "dropped_count": self._dropped_count},
            )
            # Critical events should not be silently dropped
            if isinstance(event, ExitSignalEvent):
                raise EventBusError(
                    f"Failed to publish critical ExitSignalEvent: queue full after {self._dropped_count} drops"
                )

    async def subscribe(self) -> Optional[object]:
        """Get next event from queue (blocking).

        Returns:
            Event object or None if bus stopped
        """
        if not self.running:
            return None

        try:
            event = await asyncio.wait_for(self.queue.get(), timeout=1.0)
            return event
        except asyncio.TimeoutError:
            return None

>>>>>>> 7e787d8 (Clean trading bot implementation)
    async def start(self) -> None:
        """Start the bus."""
        self.running = True

    async def stop(self) -> None:
        """Stop the bus and drain queue."""
        self.running = False

        # Drain remaining events (5s timeout)
        try:
            async with asyncio.timeout(5):
                while not self.queue.empty():
                    await self.queue.get()
        except asyncio.TimeoutError:
            pass

    def size(self) -> int:
        """Get current queue size."""
        return self.queue.qsize()
<<<<<<< HEAD
=======

    @property
    def dropped_count(self) -> int:
        """Get total dropped events count."""
        return self._dropped_count
>>>>>>> 7e787d8 (Clean trading bot implementation)
