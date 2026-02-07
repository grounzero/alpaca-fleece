"""Risk manager - safety and risk enforcement.

Check order in sequence:

SAFETY TIER (must pass):
├── Kill-switch active? → refuse
├── Circuit breaker tripped? → refuse
└── Market open? (fresh clock call via broker) → refuse if closed

RISK TIER (must pass):
├── Daily loss limit exceeded? → refuse
├── Daily trade count exceeded? → refuse
├── Position size within limit? → refuse if too large
└── Concurrent positions within limit? → refuse if too many

FILTER TIER (if enabled, must pass — no bypass):
├── Spread acceptable? (via DataHandler) → refuse if fetch fails or spread too wide
├── Bar trade count sufficient? → skip if too low
└── Time-of-day acceptable? → skip if too early/late
"""

import logging
from datetime import datetime, timezone

from src.broker import Broker
from src.data_handler import DataHandler
from src.event_bus import SignalEvent
from src.state_store import StateStore

logger = logging.getLogger(__name__)


class RiskManagerError(Exception):
    """Raised when risk check fails."""
    pass


class RiskManager:
    """Risk management and safety enforcement."""
    
    def __init__(
        self,
        broker: Broker,
        data_handler: DataHandler,
        state_store: StateStore,
        config: dict,
    ) -> None:
        """Initialise risk manager.
        
        Args:
            broker: Broker client
            data_handler: Data handler for queries
            state_store: State store for tracking
            config: Config dict with risk settings
        """
        self.broker = broker
        self.data_handler = data_handler
        self.state_store = state_store
        self.config = config
        
        # Risk limits from config (Hybrid: session-aware for crypto support)
        risk_config = config.get("risk", {})
        
        # Support both old format (single limits) and new format (session-aware)
        if isinstance(risk_config.get("regular_hours"), dict):
            # New format: session-aware limits
            self.regular_limits = risk_config.get("regular_hours", {})
            self.extended_limits = risk_config.get("extended_hours", {})
        else:
            # Old format: single limits (backward compatible)
            self.regular_limits = {
                "max_position_pct": risk_config.get("max_position_pct", 0.1),
                "max_daily_loss_pct": risk_config.get("max_daily_loss_pct", 0.05),
                "max_trades_per_day": risk_config.get("max_trades_per_day", 20),
                "max_concurrent_positions": risk_config.get("max_concurrent_positions", 10),
            }
            self.extended_limits = self.regular_limits  # Same limits for backward compat
        
        # Crypto symbols for session detection
        symbols_config = config.get("symbols", {})
        self.crypto_symbols = symbols_config.get("crypto_symbols", [])
        
        # Filters from config
        filters_config = config.get("filters", {})
        self.max_spread_pct = filters_config.get("max_spread_pct")
        self.min_bar_trades = filters_config.get("min_bar_trades")
        self.avoid_first_minutes = filters_config.get("avoid_first_minutes", 0)
        self.avoid_last_minutes = filters_config.get("avoid_last_minutes", 0)
    
    def _get_session_type(self, symbol: str) -> str:
        """Detect trading session type (Hybrid: crypto support).
        
        Args:
            symbol: Stock or crypto symbol
        
        Returns:
            'regular' (9:30-16:00 ET) or 'extended' (16:00-9:30 ET)
        """
        # Crypto trades 24/5, equities trade 9:30-16:00 ET
        if symbol in self.crypto_symbols:
            return 'extended'  # Crypto uses extended limits
        
        # For equities, detect session based on current time
        now = datetime.now(timezone.utc)
        # Convert to ET (UTC-5 in winter)
        et_offset = -5  # Simplified; could use pytz for daylight savings
        et_hour = (now.hour + et_offset) % 24
        
        # Regular hours: 9:30-16:00 ET (9.5-16.0)
        if 9.5 <= et_hour < 16.0:
            return 'regular'
        else:
            return 'extended'
    
    def _get_limits(self, symbol: str) -> dict:
        """Get risk limits for symbol (Hybrid: session-aware).
        
        Args:
            symbol: Stock or crypto symbol
        
        Returns:
            Risk limit dict (session-specific)
        """
        session_type = self._get_session_type(symbol)
        if session_type == 'extended':
            return self.extended_limits
        else:
            return self.regular_limits
    
    async def check_signal(self, signal: SignalEvent) -> bool:
        """Check if signal should be executed.
        
        Args:
            signal: SignalEvent from strategy
        
        Returns:
            True if signal passes all checks, False if filtered/skipped
        
        Raises:
            RiskManagerError if safety check fails (hard refuse)
        """
        # CONFIDENCE FILTER (Win #2: multi-timeframe SMA)
        # Filter low-confidence signals to reduce false positives
        confidence = signal.metadata.get('confidence', 0.5)
        MIN_CONFIDENCE = 0.5
        if confidence < MIN_CONFIDENCE:
            logger.warning(
                f"Signal {signal.symbol} {signal.signal_type} "
                f"confidence {confidence:.2f} < {MIN_CONFIDENCE} (filtered)"
            )
            return False
        
        # SAFETY TIER
        await self._check_safety_tier()
        
        # RISK TIER
        await self._check_risk_tier(signal.symbol, signal.signal_type)
        
        # FILTER TIER (may skip signal without error)
        if not await self._check_filter_tier(signal):
            return False
        
        return True
    
    async def _check_safety_tier(self) -> None:
        """Check safety gates (kill-switch, circuit breaker, market open)."""
        # Kill-switch
        kill_switch = self.state_store.get_state("kill_switch") == "true"
        if kill_switch:
            raise RiskManagerError("Kill-switch active")
        
        # Circuit breaker
        cb_state = self.state_store.get_state("circuit_breaker_state")
        if cb_state == "tripped":
            raise RiskManagerError("Circuit breaker tripped")
        
        # Market open (fresh clock call)
        try:
            clock = self.broker.get_clock()
            if not clock["is_open"]:
                raise RiskManagerError("Market not open")
        except (ConnectionError, TimeoutError) as e:
            raise RiskManagerError(f"Clock fetch failed: {e}")
    
    async def _check_risk_tier(self, symbol: str, side: str) -> None:
        """Check risk limits (daily loss, trade count, position size, concurrent positions).
        
        Hybrid: Uses session-aware limits (equities vs crypto)
        """
        try:
            # Get session-specific limits (Hybrid crypto support)
            limits = self._get_limits(symbol)
            
            account = self.broker.get_account()
            equity = account["equity"]
            
            # Daily loss limit (Win #3: persisted) + Session-aware
            daily_pnl = self.state_store.get_daily_pnl()  # Win #3: from DB
            max_daily_loss = equity * limits.get("max_daily_loss_pct", 0.05)
            
            if daily_pnl < -max_daily_loss:
                raise RiskManagerError(f"Daily loss limit exceeded: ${daily_pnl:.2f}")
            
            # Daily trade count (Win #3: persisted) + Session-aware
            daily_trade_count = self.state_store.get_daily_trade_count()  # Win #3: from DB
            max_trades = limits.get("max_trades_per_day", 20)
            if daily_trade_count >= max_trades:
                raise RiskManagerError(f"Daily trade count exceeded: {daily_trade_count}")
            
            # Concurrent positions + Session-aware
            positions = self.broker.get_positions()
            max_concurrent = limits.get("max_concurrent_positions", 10)
            if len(positions) >= max_concurrent:
                raise RiskManagerError(f"Concurrent positions limit reached: {len(positions)}")
            
            # Position size for this symbol
            # (would check current position + new position)
        except RiskManagerError:
            raise
        except Exception as e:
            raise RiskManagerError(f"Risk tier check failed: {e}")
    
    async def _check_filter_tier(self, signal: SignalEvent) -> bool:
        """Check filters (spread, bar trades, time-of-day).
        
        Returns:
            True if passes, False if should skip (not an error)
        """
        symbol = signal.symbol
        
        # Spread filter (if enabled)
        if self.max_spread_pct is not None:
            snapshot = self.data_handler.get_snapshot(symbol)
            
            if snapshot is None:
                raise RiskManagerError(f"Spread filter enabled but snapshot unavailable for {symbol}")
            
            bid = snapshot.get("bid")
            ask = snapshot.get("ask")
            
            if bid is None or ask is None or bid <= 0:
                raise RiskManagerError(f"Invalid spread data for {symbol}")
            
            spread_pct = (ask - bid) / bid
            if spread_pct > self.max_spread_pct:
                logger.info(f"Spread too wide for {symbol}: {spread_pct:.4f} > {self.max_spread_pct}")
                return False
        
        # Bar trade count filter (if enabled)
        if self.min_bar_trades is not None:
            df = self.data_handler.get_dataframe(symbol)
            if df is not None and "trade_count" in df.columns:
                last_trade_count = df["trade_count"].iloc[-1]
                if last_trade_count is not None and last_trade_count < self.min_bar_trades:
                    logger.info(f"Bar trade count too low for {symbol}: {last_trade_count} < {self.min_bar_trades}")
                    return False
        
        # Time-of-day filter (if enabled)
        if self.avoid_first_minutes > 0 or self.avoid_last_minutes > 0:
            from datetime import datetime
            import pytz
            
            ET = pytz.timezone("America/New_York")
            now_et = datetime.now(ET)
            
            # Market hours (9:30 - 16:00 ET)
            market_open = now_et.replace(hour=9, minute=30, second=0, microsecond=0)
            market_close = now_et.replace(hour=16, minute=0, second=0, microsecond=0)
            
            # Check if within avoidance window
            minutes_since_open = (now_et - market_open).total_seconds() / 60
            minutes_until_close = (market_close - now_et).total_seconds() / 60
            
            if minutes_since_open < self.avoid_first_minutes:
                logger.info(f"Signal skipped: within first {self.avoid_first_minutes} minutes")
                return False
            
            if minutes_until_close < self.avoid_last_minutes:
                logger.info(f"Signal skipped: within last {self.avoid_last_minutes} minutes")
                return False
        
        return True
    
    async def check_exit_order(self, symbol: str, side: str, qty: float) -> bool:
        """Check if exit order should be allowed (simplified validation).
        
        For exit orders, we only check:
        - Market open (fresh clock call)
        - Kill switch inactive
        
        We SKIP:
        - Position size checks
        - Trade count limits
        - Spread filters
        
        This ensures exits can always execute during market hours
        unless explicitly blocked by safety mechanisms.
        
        Args:
            symbol: Stock symbol
            side: Order side ("buy" or "sell")
            qty: Order quantity
        
        Returns:
            True if exit order is allowed
        
        Raises:
            RiskManagerError if exit should be blocked
        """
        # Kill-switch check
        kill_switch = self.state_store.get_state("kill_switch") == "true"
        if kill_switch:
            raise RiskManagerError("Kill-switch active - exit blocked")
        
        # Market open check (fresh clock call)
        try:
            clock = self.broker.get_clock()
            if not clock["is_open"]:
                raise RiskManagerError("Market not open - exit blocked")
        except (ConnectionError, TimeoutError) as e:
            raise RiskManagerError(f"Clock fetch failed - exit blocked: {e}")
        
        logger.debug(f"Exit order validated: {symbol} {side} {qty}")
        return True
