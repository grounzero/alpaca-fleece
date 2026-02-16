"""Configuration loading and validation."""

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, TypedDict

import yaml
from dotenv import load_dotenv


class ConfigError(Exception):
    """Raised when config is invalid."""

    pass


class EnvConfig(TypedDict, total=False):
    """Environment configuration with type safety."""

    ALPACA_API_KEY: str
    ALPACA_SECRET_KEY: str
    ALPACA_PAPER: bool
    ALLOW_LIVE_TRADING: bool
    KILL_SWITCH: bool
    CIRCUIT_BREAKER_RESET: bool
    DRY_RUN: bool
    LOG_LEVEL: str
    DATABASE_PATH: str
    CONFIG_PATH: str


@dataclass
class ExitConfig:
    """Exit manager configuration."""

    stop_loss_pct: float = 0.01
    profit_target_pct: float = 0.02
    trailing_stop_enabled: bool = False
    trailing_stop_activation_pct: float = 0.01
    trailing_stop_trail_pct: float = 0.005
    check_interval_seconds: int = 30
    exit_on_circuit_breaker: bool = True

    @classmethod
    def from_dict(cls, config: dict[str, Any]) -> "ExitConfig":
        """Create ExitConfig from config dict."""
        return cls(
            stop_loss_pct=config.get("stop_loss_pct", 0.01),
            profit_target_pct=config.get("profit_target_pct", 0.02),
            trailing_stop_enabled=config.get("trailing_stop_enabled", False),
            trailing_stop_activation_pct=config.get("trailing_stop_activation_pct", 0.01),
            trailing_stop_trail_pct=config.get("trailing_stop_trail_pct", 0.005),
            check_interval_seconds=config.get("check_interval_seconds", 30),
            exit_on_circuit_breaker=config.get("exit_on_circuit_breaker", True),
        )


def load_env() -> EnvConfig:
    """Load environment variables from .env file and environment.

    Returns:
        EnvConfig with all environment settings
    """
    load_dotenv()
    return EnvConfig(
        ALPACA_API_KEY=os.getenv("ALPACA_API_KEY", ""),
        ALPACA_SECRET_KEY=os.getenv("ALPACA_SECRET_KEY", ""),
        ALPACA_PAPER=os.getenv("ALPACA_PAPER", "true").lower() == "true",
        ALLOW_LIVE_TRADING=os.getenv("ALLOW_LIVE_TRADING", "false").lower() == "true",
        KILL_SWITCH=os.getenv("KILL_SWITCH", "false").lower() == "true",
        CIRCUIT_BREAKER_RESET=os.getenv("CIRCUIT_BREAKER_RESET", "false").lower() == "true",
        DRY_RUN=os.getenv("DRY_RUN", "false").lower() == "true",
        LOG_LEVEL=os.getenv("LOG_LEVEL", "INFO"),
        DATABASE_PATH=os.getenv("DATABASE_PATH", "data/trades.db"),
        CONFIG_PATH=os.getenv("CONFIG_PATH", "config/trading.yaml"),
    )


def load_trading_config(path: str) -> dict[str, Any]:
    """Load trading config from YAML file."""
    config_path = Path(path)

    if not config_path.exists():
        raise ConfigError(f"Config file not found: {path}")

    with open(config_path) as f:
        config: dict[str, Any] = yaml.safe_load(f) or {}

    if not config:
        raise ConfigError(f"Config file is empty: {path}")

    return config


def validate_exit_config(exit_config: dict[str, Any]) -> None:
    """Validate exit manager configuration.

    Args:
        exit_config: Dictionary containing exit configuration values

    Raises:
        ConfigError: If any exit configuration value is invalid
    """
    # Validate that exit_config is a mapping
    if not isinstance(exit_config, dict):
        raise ConfigError(
            f"trading.exits (exit_config) must be a mapping/dict, got {type(exit_config).__name__}"
        )

    # Coerce and validate numeric fields, raising ConfigError on bad types
    try:
        stop_loss = float(exit_config.get("stop_loss_pct", 0.01))
        profit_target = float(exit_config.get("profit_target_pct", 0.02))
        trailing_activation = float(exit_config.get("trailing_stop_activation_pct", 0.01))
        trailing_trail = float(exit_config.get("trailing_stop_trail_pct", 0.005))
    except (TypeError, ValueError) as e:
        raise ConfigError(f"Exit config numeric fields must be numbers: {e}") from e

    if not 0 < stop_loss < 1:
        raise ConfigError(f"stop_loss_pct must be between 0 and 1, got {stop_loss}")
    if not 0 < profit_target < 1:
        raise ConfigError(f"profit_target_pct must be between 0 and 1, got {profit_target}")
    if trailing_trail >= stop_loss:
        raise ConfigError(
            f"trailing_stop_trail_pct ({trailing_trail}) must be less than stop_loss_pct ({stop_loss})"
        )
    if trailing_activation <= 0:
        raise ConfigError("trailing_stop_activation_pct must be positive")


def validate_config(env: EnvConfig, trading: dict[str, Any]) -> None:
    """Validate configuration for safety gates and consistency."""

    # Safety gates
    if not env["ALPACA_API_KEY"]:
        raise ConfigError("ALPACA_API_KEY not set")
    if not env["ALPACA_SECRET_KEY"]:
        raise ConfigError("ALPACA_SECRET_KEY not set")

    # Live trading requires dual gates
    if not env["ALPACA_PAPER"] and not env["ALLOW_LIVE_TRADING"]:
        raise ConfigError(
            "Live trading requires dual gates: BOTH ALPACA_PAPER=false AND ALLOW_LIVE_TRADING=true"
        )

    # Kill switch
    kill_switch_env = env.get("KILL_SWITCH", False)
    kill_switch_file = Path(".kill_switch").exists()
    if kill_switch_env or kill_switch_file:
        raise ConfigError("Kill switch active; trading refused")

    # Validate trading config structure
    required_sections = ["symbols", "trading", "strategy", "risk", "execution"]
    for section in required_sections:
        if section not in trading:
            raise ConfigError(f"Missing trading config section: {section}")

    # Validate symbols
    symbols_config = trading.get("symbols", {})
    mode = symbols_config.get("mode", "explicit")
    if mode not in ["explicit", "watchlist", "screener"]:
        raise ConfigError(f"Invalid symbols.mode: {mode}")

    if mode == "explicit":
        has_equity_symbols = bool(symbols_config.get("equity_symbols"))
        has_crypto_symbols = bool(symbols_config.get("crypto_symbols"))

        if not (has_equity_symbols or has_crypto_symbols):
            raise ConfigError(
                "symbols.mode=explicit requires at least one of symbols.equity_symbols, "
                "symbols.crypto_symbols"
            )
    # Validate strategy
    strategy_config = trading.get("strategy", {})
    if not strategy_config.get("name"):
        raise ConfigError("strategy.name not set")

    # Validate trading session policy
    trading_config = trading.get("trading", {})
    session_policy = trading_config.get("session_policy")
    if session_policy not in ["regular_only", "include_extended"]:
        raise ConfigError(f"Invalid session_policy: {session_policy}")

    # Validate exit configuration
    exits_config = trading.get("exits", {})
    validate_exit_config(exits_config)

    # Validate asset scope (US equities only)
    # This will be checked at runtime when symbols are resolved
