"""Tests for configuration validation."""

import pytest
from src.config import validate_config, ConfigError


def test_validate_config_requires_api_key():
    """Validation fails if API key missing."""
    env = {
        "ALPACA_API_KEY": "",
        "ALPACA_SECRET_KEY": "secret",
        "ALPACA_PAPER": True,
        "ALLOW_LIVE_TRADING": False,
        "KILL_SWITCH": False,
        "CIRCUIT_BREAKER_RESET": False,
        "DRY_RUN": False,
        "LOG_LEVEL": "INFO",
        "DATABASE_PATH": "data/trades.db",
        "CONFIG_PATH": "config/trading.yaml",
    }

    trading = {
        "symbols": {"mode": "explicit", "list": ["AAPL"]},
        "trading": {"session_policy": "regular_only"},
        "strategy": {"name": "sma_crossover"},
        "risk": {"max_position_pct": 0.1},
        "execution": {"order_type": "market"},
    }

    with pytest.raises(ConfigError, match="ALPACA_API_KEY"):
        validate_config(env, trading)


def test_validate_config_requires_secret_key():
    """Validation fails if secret key missing."""
    env = {
        "ALPACA_API_KEY": "key",
        "ALPACA_SECRET_KEY": "",
        "ALPACA_PAPER": True,
        "ALLOW_LIVE_TRADING": False,
        "KILL_SWITCH": False,
        "CIRCUIT_BREAKER_RESET": False,
        "DRY_RUN": False,
        "LOG_LEVEL": "INFO",
        "DATABASE_PATH": "data/trades.db",
        "CONFIG_PATH": "config/trading.yaml",
    }

    trading = {
        "symbols": {"mode": "explicit", "list": ["AAPL"]},
        "trading": {"session_policy": "regular_only"},
        "strategy": {"name": "sma_crossover"},
        "risk": {"max_position_pct": 0.1},
        "execution": {"order_type": "market"},
    }

    with pytest.raises(ConfigError, match="ALPACA_SECRET_KEY"):
        validate_config(env, trading)


def test_validate_config_live_trading_requires_dual_gates():
    """Live trading requires BOTH ALPACA_PAPER=false AND ALLOW_LIVE_TRADING=true."""
    env = {
        "ALPACA_API_KEY": "key",
        "ALPACA_SECRET_KEY": "secret",
        "ALPACA_PAPER": False,  # Live
        "ALLOW_LIVE_TRADING": False,  # But not allowed!
        "KILL_SWITCH": False,
        "CIRCUIT_BREAKER_RESET": False,
        "DRY_RUN": False,
        "LOG_LEVEL": "INFO",
        "DATABASE_PATH": "data/trades.db",
        "CONFIG_PATH": "config/trading.yaml",
    }

    trading = {
        "symbols": {"mode": "explicit", "list": ["AAPL"]},
        "trading": {"session_policy": "regular_only"},
        "strategy": {"name": "sma_crossover"},
        "risk": {"max_position_pct": 0.1},
        "execution": {"order_type": "market"},
    }

    with pytest.raises(ConfigError, match="dual gates"):
        validate_config(env, trading)


def test_validate_config_detects_kill_switch():
    """Validation fails if kill-switch is active."""
    env = {
        "ALPACA_API_KEY": "key",
        "ALPACA_SECRET_KEY": "secret",
        "ALPACA_PAPER": True,
        "ALLOW_LIVE_TRADING": False,
        "KILL_SWITCH": True,  # Active!
        "CIRCUIT_BREAKER_RESET": False,
        "DRY_RUN": False,
        "LOG_LEVEL": "INFO",
        "DATABASE_PATH": "data/trades.db",
        "CONFIG_PATH": "config/trading.yaml",
    }

    trading = {
        "symbols": {"mode": "explicit", "list": ["AAPL"]},
        "trading": {"session_policy": "regular_only"},
        "strategy": {"name": "sma_crossover"},
        "risk": {"max_position_pct": 0.1},
        "execution": {"order_type": "market"},
    }

    with pytest.raises(ConfigError, match="Kill switch"):
        validate_config(env, trading)


def test_validate_config_valid_passes():
    """Valid config passes validation."""
    env = {
        "ALPACA_API_KEY": "key",
        "ALPACA_SECRET_KEY": "secret",
        "ALPACA_PAPER": True,
        "ALLOW_LIVE_TRADING": False,
        "KILL_SWITCH": False,
        "CIRCUIT_BREAKER_RESET": False,
        "DRY_RUN": False,
        "LOG_LEVEL": "INFO",
        "DATABASE_PATH": "data/trades.db",
        "CONFIG_PATH": "config/trading.yaml",
    }

    trading = {
        "symbols": {"mode": "explicit", "list": ["AAPL"]},
        "trading": {"session_policy": "regular_only"},
        "strategy": {"name": "sma_crossover"},
        "risk": {"max_position_pct": 0.1},
        "execution": {"order_type": "market"},
    }

    # Should not raise
    validate_config(env, trading)
