"""Event bus for asynchronous message passing."""
import asyncio
import logging
from dataclasses import dataclass
from datetime import datetime
from typing import Any, Callable, Dict, List, Optional, Type
from enum import Enum


class EventType(Enum):
    """Event type enumeration."""
    MARKET_BAR = "market_bar"
    SIGNAL = "signal"
    ORDER_INTENT = "order_intent"
    ORDER_UPDATE = "order_update"


@dataclass
class BaseEvent:
    """Base event class."""
    event_type: EventType
    timestamp: datetime


@dataclass
class MarketBarEvent(BaseEvent):
    """Market bar event from WebSocket stream."""
    symbol: str
    open: float
    high: float
    low: float
    close: float
    volume: int
    bar_timestamp: datetime
    vwap: Optional[float] = None

    def __init__(
        self,
        symbol: str,
        open: float,
        high: float,
        low: float,
        close: float,
        volume: int,
        bar_timestamp: datetime,
        vwap: Optional[float] = None,
    ):
        super().__init__(event_type=EventType.MARKET_BAR, timestamp=datetime.utcnow())
        self.symbol = symbol
        self.open = open
        self.high = high
        self.low = low
        self.close = close
        self.volume = volume
        self.bar_timestamp = bar_timestamp
        self.vwap = vwap


@dataclass
class SignalEvent(BaseEvent):
    """Trading signal event."""
    symbol: str
    side: str  # "buy" or "sell"
    strategy_name: str
    signal_timestamp: datetime
    metadata: Optional[Dict[str, Any]] = None

    def __init__(
        self,
        symbol: str,
        side: str,
        strategy_name: str,
        signal_timestamp: datetime,
        metadata: Optional[Dict[str, Any]] = None,
    ):
        super().__init__(event_type=EventType.SIGNAL, timestamp=datetime.utcnow())
        self.symbol = symbol
        self.side = side.lower()
        self.strategy_name = strategy_name
        self.signal_timestamp = signal_timestamp
        self.metadata = metadata or {}


@dataclass
class OrderIntentEvent(BaseEvent):
    """Order intent event (after risk validation)."""
    client_order_id: str
    symbol: str
    side: str
    qty: float
    order_type: str = "market"
    time_in_force: str = "day"

    def __init__(
        self,
        client_order_id: str,
        symbol: str,
        side: str,
        qty: float,
        order_type: str = "market",
        time_in_force: str = "day",
    ):
        super().__init__(event_type=EventType.ORDER_INTENT, timestamp=datetime.utcnow())
        self.client_order_id = client_order_id
        self.symbol = symbol
        self.side = side
        self.qty = qty
        self.order_type = order_type
        self.time_in_force = time_in_force


@dataclass
class OrderUpdateEvent(BaseEvent):
    """Order status update event."""
    client_order_id: str
    alpaca_order_id: str
    symbol: str
    side: str
    status: str
    filled_qty: float
    filled_avg_price: Optional[float] = None

    def __init__(
        self,
        client_order_id: str,
        alpaca_order_id: str,
        symbol: str,
        side: str,
        status: str,
        filled_qty: float,
        filled_avg_price: Optional[float] = None,
    ):
        super().__init__(event_type=EventType.ORDER_UPDATE, timestamp=datetime.utcnow())
        self.client_order_id = client_order_id
        self.alpaca_order_id = alpaca_order_id
        self.symbol = symbol
        self.side = side
        self.status = status
        self.filled_qty = filled_qty
        self.filled_avg_price = filled_avg_price


class EventBus:
    """Async event bus using asyncio.Queue."""

    def __init__(self, maxsize: int = 1000):
        """
        Set up event bus.

        Args:
            maxsize: Maximum queue size (0 = unlimited)
        """
        self.queue: asyncio.Queue = asyncio.Queue(maxsize=maxsize)
        self.handlers: Dict[EventType, List[Callable]] = {event_type: [] for event_type in EventType}
        self.running = False
        self._task: Optional[asyncio.Task] = None

    def subscribe(self, event_type: EventType, handler: Callable):
        """
        Subscribe a handler to an event type.

        Args:
            event_type: Type of event to listen for
            handler: Async callable that takes an event as argument
        """
        if handler not in self.handlers[event_type]:
            self.handlers[event_type].append(handler)

    def unsubscribe(self, event_type: EventType, handler: Callable):
        """
        Unsubscribe a handler from an event type.

        Args:
            event_type: Type of event
            handler: Handler to remove
        """
        if handler in self.handlers[event_type]:
            self.handlers[event_type].remove(handler)

    async def publish(self, event: BaseEvent):
        """
        Publish an event to the bus.

        Args:
            event: Event to publish
        """
        await self.queue.put(event)

    async def run(self):
        """Process events from the queue and dispatch to handlers."""
        self.running = True

        while self.running:
            try:
                # Wait for event with timeout to allow checking running flag
                event = await asyncio.wait_for(self.queue.get(), timeout=1.0)

                # Dispatch to all registered handlers for this event type
                handlers = self.handlers.get(event.event_type, [])

                for handler in handlers:
                    try:
                        if asyncio.iscoroutinefunction(handler):
                            await handler(event)
                        else:
                            handler(event)
                    except Exception:
                        # Log error but don't stop processing
                        logger = logging.getLogger(__name__)
                        logger.exception(
                            f"Error in event handler for {event.event_type}",
                            extra={"event": str(event)}
                        )

                self.queue.task_done()

            except asyncio.TimeoutError:
                # No event in queue, continue
                continue
            except Exception:
                logger = logging.getLogger(__name__)
                logger.exception("Error processing event")

    async def stop(self, timeout: float = 5.0):
        """
        Stop the event bus.

        Args:
            timeout: How long to wait for queue to drain
        """
        self.running = False

        # Wait for queue to be processed
        try:
            await asyncio.wait_for(self.queue.join(), timeout=timeout)
        except asyncio.TimeoutError:
            logger = logging.getLogger(__name__)
            logger.warning(f"Event queue did not drain within {timeout}s, {self.queue.qsize()} events remaining")

    def start_task(self) -> asyncio.Task:
        """Start the event bus as an asyncio task."""
        if self._task is None or self._task.done():
            self._task = asyncio.create_task(self.run())
        return self._task

    async def wait_for_completion(self):
        """Wait for the event bus task to complete."""
        if self._task:
            await self._task
