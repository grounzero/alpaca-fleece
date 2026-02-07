"""Event bus - asyncio.Queue for event flow.

Single queue with publish/subscribe pattern.
All internal events flow through here.
"""

import asyncio
from dataclasses import dataclass
from datetime import datetime
from typing import Optional


# Event types
@dataclass(frozen=True)
class BarEvent:
    """Market bar received and persisted."""
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
    metadata: dict


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
        self.queue: asyncio.Queue = asyncio.Queue(maxsize=maxsize)
        self.running = False
    
    async def publish(self, event) -> None:
        """Publish event to queue.
        
        Args:
            event: Event object (BarEvent, SignalEvent, etc)
        """
        if not self.running:
            return
        
        try:
            await asyncio.wait_for(self.queue.put(event), timeout=5.0)
        except asyncio.TimeoutError:
            # Queue full, skip (log elsewhere)
            pass
    
    async def subscribe(self) -> Optional:
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
