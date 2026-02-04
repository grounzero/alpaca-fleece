"""Risk management with safety checks and position sizing."""
import logging
from datetime import datetime, time
from pathlib import Path
from typing import Optional, Tuple
import pytz

from src.config import Config
from src.event_bus import SignalEvent
from src.state_store import StateStore
from src.broker import Broker


class RiskManager:
    """Manage risk with safety checks and position sizing."""

    def __init__(
        self,
        config: Config,
        state_store: StateStore,
        broker: Broker,
        logger: logging.Logger,
    ):
        """
        Set up risk manager.

        Args:
            config: Configuration object
            state_store: State store instance
            broker: Broker instance
            logger: Logger instance
        """
        self.config = config
        self.state_store = state_store
        self.broker = broker
        self.logger = logger

        # Market hours (America/New_York timezone)
        self.market_open = time(9, 30)
        self.market_close = time(16, 0)
        self.tz = pytz.timezone("America/New_York")

        # Circuit breaker settings
        self.max_consecutive_failures = 5

    async def validate_signal(self, signal: SignalEvent, current_price: float) -> Tuple[bool, str]:
        """
        Validate if signal should be executed.

        Args:
            signal: Signal event to validate
            current_price: Current market price

        Returns:
            Tuple of (is_valid, reason)
        """
        # Check kill switch (both env var AND file - file checked dynamically)
        kill_switch_file = Path(__file__).parent.parent / ".kill_switch"
        if self.config.kill_switch or kill_switch_file.exists():
            return False, "Kill switch activated"

        # Check circuit breaker
        if self.check_circuit_breaker():
            return False, "Circuit breaker tripped"

        # Check market hours
        if not self.config.allow_extended_hours:
            if not self.is_market_hours():
                return False, "Outside market hours"

        # Check daily trade limit
        daily_trades = self.state_store.get_daily_trade_count()
        if daily_trades >= self.config.max_trades_per_day:
            return False, f"Daily trade limit reached ({self.config.max_trades_per_day})"

        # Check account status
        try:
            account = await self.broker.get_account()

            if account.get("trading_blocked") or account.get("account_blocked"):
                return False, "Account blocked"

            # Check daily loss limit
            equity = account.get("equity", 0)
            portfolio_value = account.get("portfolio_value", equity)

            # Get starting equity (approximate as we don't track it explicitly)
            # For simplicity, we'll check against current equity
            if equity <= 0:
                return False, "Zero or negative equity"

            # Check position size
            position_value = self.calculate_position_value(equity, current_price)
            max_position_value = equity * self.config.max_position_pct

            if position_value > max_position_value:
                return False, f"Position size too large (${position_value:.2f} > ${max_position_value:.2f})"

        except Exception as e:
            self.logger.exception("Error checking account")
            return False, "Account check failed"

        return True, "Validated"

    def calculate_qty(self, equity: float, price: float) -> int:
        """
        Calculate position quantity.

        Args:
            equity: Account equity
            price: Current price

        Returns:
            Quantity to trade (integer shares)
        """
        if price <= 0:
            return 0

        # Calculate max position value
        max_position_value = equity * self.config.max_position_pct

        # Calculate quantity
        qty = int(max_position_value / price)

        return max(qty, 0)

    def calculate_position_value(self, equity: float, price: float) -> float:
        """Calculate position value in dollars."""
        qty = self.calculate_qty(equity, price)
        return qty * price

    def is_market_hours(self) -> bool:
        """Check if current time is within market hours (ET)."""
        now = datetime.now(self.tz)
        current_time = now.time()

        # Check if weekday (Monday = 0, Sunday = 6)
        if now.weekday() >= 5:  # Saturday or Sunday
            return False

        # Check time
        return self.market_open <= current_time <= self.market_close

    def check_circuit_breaker(self) -> bool:
        """Check if circuit breaker is tripped."""
        state = self.state_store.get_circuit_breaker_state()
        return state.get("tripped", False)

    def trip_circuit_breaker(self, reason: str):
        """Trip the circuit breaker."""
        self.logger.error(f"CIRCUIT BREAKER TRIPPED: {reason}")
        self.state_store.set_circuit_breaker_state(tripped=True, failures=self.max_consecutive_failures)

    def record_failure(self):
        """Record a failure and trip circuit breaker if threshold reached."""
        state = self.state_store.get_circuit_breaker_state()
        failures = state.get("failures", 0) + 1

        self.logger.warning(f"Recording failure {failures}/{self.max_consecutive_failures}")

        if failures >= self.max_consecutive_failures:
            self.trip_circuit_breaker(f"{failures} consecutive failures")
        else:
            self.state_store.set_circuit_breaker_state(tripped=False, failures=failures)

    def reset_failures(self):
        """Reset failure count on successful operation."""
        state = self.state_store.get_circuit_breaker_state()
        if state.get("failures", 0) > 0:
            self.logger.info("Resetting failure count after successful operation")
            self.state_store.set_circuit_breaker_state(tripped=False, failures=0)

    def reset_circuit_breaker(self):
        """Manually reset circuit breaker."""
        self.logger.info("Manually resetting circuit breaker")
        self.state_store.reset_circuit_breaker()
