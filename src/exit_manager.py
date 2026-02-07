"""Exit manager - monitor positions and generate exit signals.

Responsibilities:
- Monitor open positions periodically (every check_interval_seconds)
- Calculate P&L for each position against entry price
- Evaluate exit rules: stop loss (-1%), profit target (+2%), trailing stop (optional)
- Generate ExitSignalEvent when thresholds breached
- Track trailing stop levels per position
- Provide close_all_positions() for emergency exits

Exit Rules Priority:
1. Stop loss (highest priority) - IF unrealised_pnl_pct <= -stop_loss_pct
2. Trailing stop (if activated) - IF current_price <= trailing_stop_price
3. Profit target - IF unrealised_pnl_pct >= profit_target_pct
"""

import asyncio
import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Optional

from src.broker import Broker
from src.position_tracker import PositionTracker
from src.event_bus import EventBus
from src.state_store import StateStore
from src.data_handler import DataHandler

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class ExitSignalEvent:
    """Exit signal for position management.
    
    Published when exit threshold is breached.
    """
    symbol: str
    side: str  # "sell" for long positions, "buy" for short positions
    qty: float
    reason: str  # "stop_loss", "trailing_stop", "profit_target", "emergency"
    entry_price: float
    current_price: float
    pnl_pct: float
    pnl_amount: float
    timestamp: datetime


class ExitManagerError(Exception):
    """Raised when exit manager operation fails."""
    pass


class ExitManager:
    """Monitor positions and generate exit signals."""
    
    def __init__(
        self,
        broker: Broker,
        position_tracker: PositionTracker,
        event_bus: EventBus,
        state_store: StateStore,
        data_handler: DataHandler,
        stop_loss_pct: float = 0.01,
        profit_target_pct: float = 0.02,
        trailing_stop_enabled: bool = False,
        trailing_stop_activation_pct: float = 0.01,
        trailing_stop_trail_pct: float = 0.005,
        check_interval_seconds: int = 30,
        exit_on_circuit_breaker: bool = True,
    ) -> None:
        """Initialise exit manager.
        
        Args:
            broker: Broker client for price fetching
            position_tracker: Position tracker for entry prices
            event_bus: Event bus for publishing exit signals
            state_store: State store for persistence
            data_handler: Data handler for price data
            stop_loss_pct: Stop loss percentage (e.g., 0.01 = -1%)
            profit_target_pct: Profit target percentage (e.g., 0.02 = +2%)
            trailing_stop_enabled: Whether trailing stops are enabled
            trailing_stop_activation_pct: P&L % to activate trailing stop
            trailing_stop_trail_pct: Distance below highest price for trailing stop
            check_interval_seconds: How often to check positions
            exit_on_circuit_breaker: Whether to close all positions on circuit breaker
        """
        self.broker = broker
        self.position_tracker = position_tracker
        self.event_bus = event_bus
        self.state_store = state_store
        self.data_handler = data_handler
        
        self.stop_loss_pct = stop_loss_pct
        self.profit_target_pct = profit_target_pct
        self.trailing_stop_enabled = trailing_stop_enabled
        self.trailing_stop_activation_pct = trailing_stop_activation_pct
        self.trailing_stop_trail_pct = trailing_stop_trail_pct
        self.check_interval_seconds = check_interval_seconds
        self.exit_on_circuit_breaker = exit_on_circuit_breaker
        
        self._running = False
        self._monitor_task: Optional[asyncio.Task] = None
    
    async def start(self) -> None:
        """Start the exit manager monitoring loop."""
        if self._running:
            logger.warning("Exit manager already running")
            return
        
        logger.info("Starting exit manager...")
        logger.info(
            f"  Stop loss: -{self.stop_loss_pct*100:.1f}%, "
            f"Profit target: +{self.profit_target_pct*100:.1f}%"
        )
        if self.trailing_stop_enabled:
            logger.info(
                f"  Trailing stop: enabled (activation +{self.trailing_stop_activation_pct*100:.1f}%, "
                f"trail -{self.trailing_stop_trail_pct*100:.1f}%)"
            )
        else:
            logger.info("  Trailing stop: disabled")
        
        self._running = True
        self._monitor_task = asyncio.create_task(self._monitor_loop())
        logger.info(f"Exit manager started (checking every {self.check_interval_seconds}s)")
    
    async def stop(self) -> None:
        """Stop the exit manager monitoring loop."""
        if not self._running:
            return
        
        logger.info("Stopping exit manager...")
        self._running = False
        
        if self._monitor_task:
            self._monitor_task.cancel()
            try:
                await self._monitor_task
            except asyncio.CancelledError:
                pass
        
        logger.info("Exit manager stopped")
    
    async def _monitor_loop(self) -> None:
        """Main monitoring loop."""
        logger.info("Exit monitor loop started")
        
        while self._running:
            try:
                # Check circuit breaker first
                if self.exit_on_circuit_breaker:
                    cb_state = self.state_store.get_state("circuit_breaker_state")
                    if cb_state == "tripped":
                        logger.warning("Circuit breaker tripped - closing all positions")
                        await self.close_all_positions(reason="circuit_breaker")
                        await asyncio.sleep(self.check_interval_seconds)
                        continue
                
                # Check positions
                await self.check_positions()
                
            except Exception as e:
                logger.error(f"Error in exit monitor loop: {e}", exc_info=True)
            
            try:
                await asyncio.sleep(self.check_interval_seconds)
            except asyncio.CancelledError:
                break
        
        logger.info("Exit monitor loop ended")
    
    async def check_positions(self) -> list[ExitSignalEvent]:
        """Check all tracked positions for exit conditions.
        
        Returns:
            List of ExitSignalEvents generated
        """
        signals = []
        positions = self.position_tracker.get_all_positions()
        
        if not positions:
            return signals
        
        # Check if market is open first (fresh clock call)
        try:
            clock = self.broker.get_clock()
            if not clock["is_open"]:
                logger.debug("Market closed - skipping position checks")
                return signals
        except Exception as e:
            logger.error(f"Failed to check market clock: {e}")
            return signals
        
        for position in positions:
            try:
                # Get current price
                snapshot = self.data_handler.get_snapshot(position.symbol)
                if not snapshot:
                    logger.warning(f"No snapshot available for {position.symbol}")
                    continue
                
                current_price = snapshot.get("last_price") or snapshot.get("bid")
                if not current_price:
                    logger.warning(f"No current price available for {position.symbol}")
                    continue
                
                # Update position with current price (for trailing stop)
                self.position_tracker.update_current_price(position.symbol, current_price)
                
                # Check for exit signal
                signal = self._evaluate_exit_rules(position, current_price)
                if signal:
                    signals.append(signal)
                    await self.event_bus.publish(signal)
                    logger.info(
                        f"Exit signal: {signal.symbol} {signal.reason} "
                        f"(P&L: {signal.pnl_pct*100:.1f}%)"
                    )
            
            except Exception as e:
                logger.error(f"Error checking position {position.symbol}: {e}")
        
        return signals
    
    def _evaluate_exit_rules(
        self,
        position,
        current_price: float,
    ) -> Optional[ExitSignalEvent]:
        """Evaluate exit rules for a position.
        
        Exit Rules Priority:
        1. Stop loss (highest priority) - IF unrealised_pnl_pct <= -stop_loss_pct
        2. Trailing stop (if activated) - IF current_price <= trailing_stop_price
        3. Profit target - IF unrealised_pnl_pct >= profit_target_pct
        
        Args:
            position: PositionData
            current_price: Current market price
        
        Returns:
            ExitSignalEvent if threshold breached, None otherwise
        """
        # Calculate P&L
        pnl_amount, pnl_pct = self.position_tracker.calculate_pnl(
            position.symbol, current_price
        )
        
        # Priority 1: Stop loss (highest priority)
        if pnl_pct <= -self.stop_loss_pct:
            return ExitSignalEvent(
                symbol=position.symbol,
                side="sell" if position.side == "long" else "buy",
                qty=position.qty,
                reason="stop_loss",
                entry_price=position.entry_price,
                current_price=current_price,
                pnl_pct=pnl_pct,
                pnl_amount=pnl_amount,
                timestamp=datetime.now(timezone.utc),
            )
        
        # Priority 2: Trailing stop (if activated and enabled)
        if self.trailing_stop_enabled and position.trailing_stop_activated:
            if position.trailing_stop_price and current_price <= position.trailing_stop_price:
                return ExitSignalEvent(
                    symbol=position.symbol,
                    side="sell" if position.side == "long" else "buy",
                    qty=position.qty,
                    reason="trailing_stop",
                    entry_price=position.entry_price,
                    current_price=current_price,
                    pnl_pct=pnl_pct,
                    pnl_amount=pnl_amount,
                    timestamp=datetime.now(timezone.utc),
                )
        
        # Priority 3: Profit target
        if pnl_pct >= self.profit_target_pct:
            return ExitSignalEvent(
                symbol=position.symbol,
                side="sell" if position.side == "long" else "buy",
                qty=position.qty,
                reason="profit_target",
                entry_price=position.entry_price,
                current_price=current_price,
                pnl_pct=pnl_pct,
                pnl_amount=pnl_amount,
                timestamp=datetime.now(timezone.utc),
            )
        
        return None
    
    async def close_all_positions(self, reason: str = "emergency") -> list[ExitSignalEvent]:
        """Close all open positions immediately.
        
        Generates ExitSignalEvent for each position.
        
        Args:
            reason: Reason for closing (e.g., "emergency", "circuit_breaker", "shutdown")
        
        Returns:
            List of ExitSignalEvents generated
        """
        logger.warning(f"Closing all positions (reason: {reason})")
        
        signals = []
        positions = self.position_tracker.get_all_positions()
        
        for position in positions:
            try:
                # Get current price
                snapshot = self.data_handler.get_snapshot(position.symbol)
                current_price = snapshot.get("last_price") or snapshot.get("bid") or position.entry_price
                
                # Calculate P&L
                pnl_amount, pnl_pct = self.position_tracker.calculate_pnl(
                    position.symbol, current_price
                )
                
                signal = ExitSignalEvent(
                    symbol=position.symbol,
                    side="sell" if position.side == "long" else "buy",
                    qty=position.qty,
                    reason=reason,
                    entry_price=position.entry_price,
                    current_price=current_price,
                    pnl_pct=pnl_pct,
                    pnl_amount=pnl_amount,
                    timestamp=datetime.now(timezone.utc),
                )
                
                signals.append(signal)
                await self.event_bus.publish(signal)
                logger.info(
                    f"Emergency exit: {signal.symbol} {signal.qty} shares "
                    f"(P&L: {signal.pnl_pct*100:.1f}%)"
                )
            
            except Exception as e:
                logger.error(f"Error generating exit signal for {position.symbol}: {e}")
        
        logger.warning(f"Generated {len(signals)} exit signals for {reason}")
        return signals
    
    async def check_single_position(self, symbol: str) -> Optional[ExitSignalEvent]:
        """Check a single position for exit conditions.
        
        Args:
            symbol: Stock symbol to check
        
        Returns:
            ExitSignalEvent if threshold breached, None otherwise
        """
        position = self.position_tracker.get_position(symbol)
        if not position:
            return None
        
        # Get current price
        snapshot = self.data_handler.get_snapshot(symbol)
        if not snapshot:
            return None
        
        current_price = snapshot.get("last_price") or snapshot.get("bid")
        if not current_price:
            return None
        
        # Update position with current price
        self.position_tracker.update_current_price(symbol, current_price)
        
        # Evaluate exit rules
        return self._evaluate_exit_rules(position, current_price)
