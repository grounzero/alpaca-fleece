"""Configuration management with validation."""
import os
from dataclasses import dataclass
from pathlib import Path
from typing import List
from dotenv import load_dotenv


@dataclass(frozen=True)
class Config:
    """Immutable configuration container."""

    # Credentials
    alpaca_api_key: str
    alpaca_secret_key: str
    alpaca_paper: bool

    # Safety gate
    allow_live_trading: bool

    # Trading
    symbols: List[str]
    bar_timeframe: str
    stream_feed: str

    # Risk limits
    max_position_pct: float
    max_daily_loss_pct: float
    max_trades_per_day: int

    # Strategy
    sma_fast: int
    sma_slow: int

    # Modes
    dry_run: bool
    allow_extended_hours: bool
    log_level: str

    # Runtime
    kill_switch: bool
    circuit_breaker_reset: bool

    def is_live_trading_enabled(self) -> bool:
        """Check if live trading is enabled (requires both flags)."""
        return not self.alpaca_paper and self.allow_live_trading

    @property
    def base_url(self) -> str:
        """Get Alpaca API base URL."""
        return "https://paper-api.alpaca.markets" if self.alpaca_paper else "https://api.alpaca.markets"


def load_config() -> Config:
    """Load and validate configuration from environment."""
    # Load .env file if it exists
    env_path = Path(__file__).parent.parent / ".env"
    if env_path.exists():
        load_dotenv(env_path)

    # Check for kill switch file
    kill_switch_file = Path(__file__).parent.parent / ".kill_switch"
    kill_switch = kill_switch_file.exists() or os.getenv("KILL_SWITCH", "false").lower() == "true"

    # Required fields
    required_fields = [
        "ALPACA_API_KEY",
        "ALPACA_SECRET_KEY",
    ]

    missing = [field for field in required_fields if not os.getenv(field)]
    if missing:
        raise ValueError(f"Missing required environment variables: {', '.join(missing)}")

    # Parse symbols
    symbols_str = os.getenv("SYMBOLS", "AAPL,MSFT")
    symbols = [s.strip() for s in symbols_str.split(",") if s.strip()]

    if not symbols:
        raise ValueError("SYMBOLS must contain at least one symbol")

    # Parse numeric values
    try:
        max_position_pct = float(os.getenv("MAX_POSITION_PCT", "0.10"))
        max_daily_loss_pct = float(os.getenv("MAX_DAILY_LOSS_PCT", "0.05"))
        max_trades_per_day = int(os.getenv("MAX_TRADES_PER_DAY", "20"))
        sma_fast = int(os.getenv("SMA_FAST", "10"))
        sma_slow = int(os.getenv("SMA_SLOW", "30"))
    except ValueError as e:
        raise ValueError(f"Invalid numeric configuration value: {e}")

    # Validate ranges
    if not 0 < max_position_pct <= 1:
        raise ValueError("MAX_POSITION_PCT must be between 0 and 1")

    if not 0 < max_daily_loss_pct <= 1:
        raise ValueError("MAX_DAILY_LOSS_PCT must be between 0 and 1")

    if max_trades_per_day <= 0:
        raise ValueError("MAX_TRADES_PER_DAY must be positive")

    if sma_fast >= sma_slow:
        raise ValueError("SMA_FAST must be less than SMA_SLOW")

    # Parse boolean values
    alpaca_paper = os.getenv("ALPACA_PAPER", "true").lower() == "true"
    allow_live_trading = os.getenv("ALLOW_LIVE_TRADING", "false").lower() == "true"
    dry_run = os.getenv("DRY_RUN", "false").lower() == "true"
    allow_extended_hours = os.getenv("ALLOW_EXTENDED_HOURS", "false").lower() == "true"
    circuit_breaker_reset = os.getenv("CIRCUIT_BREAKER_RESET", "false").lower() == "true"

    # CRITICAL SAFETY: Refuse to start if live API selected but safety gate not confirmed
    # This prevents accidental live trading when user sets ALPACA_PAPER=false but forgets ALLOW_LIVE_TRADING=true
    if not alpaca_paper and not allow_live_trading:
        raise ValueError(
            "UNSAFE CONFIGURATION: ALPACA_PAPER=false but ALLOW_LIVE_TRADING=false. "
            "This would connect to the LIVE Alpaca API without explicit confirmation. "
            "Either set ALPACA_PAPER=true for paper trading, or set ALLOW_LIVE_TRADING=true "
            "to explicitly confirm you want to trade with real money."
        )

    # Validate bar timeframe
    bar_timeframe = os.getenv("BAR_TIMEFRAME", "1Min")
    valid_timeframes = ["1Min", "5Min", "15Min", "1Hour", "1Day"]
    if bar_timeframe not in valid_timeframes:
        raise ValueError(f"BAR_TIMEFRAME must be one of: {', '.join(valid_timeframes)}")

    # Validate stream feed
    stream_feed = os.getenv("STREAM_FEED", "iex")
    valid_feeds = ["iex", "sip"]
    if stream_feed not in valid_feeds:
        raise ValueError(f"STREAM_FEED must be one of: {', '.join(valid_feeds)}")

    # Validate log level
    log_level = os.getenv("LOG_LEVEL", "INFO").upper()
    valid_levels = ["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"]
    if log_level not in valid_levels:
        raise ValueError(f"LOG_LEVEL must be one of: {', '.join(valid_levels)}")

    return Config(
        alpaca_api_key=os.getenv("ALPACA_API_KEY"),
        alpaca_secret_key=os.getenv("ALPACA_SECRET_KEY"),
        alpaca_paper=alpaca_paper,
        allow_live_trading=allow_live_trading,
        symbols=symbols,
        bar_timeframe=bar_timeframe,
        stream_feed=stream_feed,
        max_position_pct=max_position_pct,
        max_daily_loss_pct=max_daily_loss_pct,
        max_trades_per_day=max_trades_per_day,
        sma_fast=sma_fast,
        sma_slow=sma_slow,
        dry_run=dry_run,
        allow_extended_hours=allow_extended_hours,
        log_level=log_level,
        kill_switch=kill_switch,
        circuit_breaker_reset=circuit_breaker_reset,
    )
