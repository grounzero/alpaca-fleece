"""Tests for configuration management."""
import os
import pytest
from pathlib import Path
from src.config import load_config, Config


def test_config_valid(monkeypatch, tmp_path):
    """Test valid configuration."""
    # Set required environment variables
    monkeypatch.setenv("ALPACA_API_KEY", "test_key_123")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret_456")
    monkeypatch.setenv("SYMBOLS", "AAPL,MSFT,GOOGL")
    monkeypatch.setenv("SMA_FAST", "5")
    monkeypatch.setenv("SMA_SLOW", "20")

    config = load_config()

    assert config.alpaca_api_key == "test_key_123"
    assert config.alpaca_secret_key == "test_secret_456"
    assert config.symbols == ["AAPL", "MSFT", "GOOGL"]
    assert config.sma_fast == 5
    assert config.sma_slow == 20
    assert config.alpaca_paper is True
    assert config.allow_live_trading is False


def test_config_missing_api_key(monkeypatch):
    """Test missing API key."""
    monkeypatch.delenv("ALPACA_API_KEY", raising=False)
    monkeypatch.delenv("ALPACA_SECRET_KEY", raising=False)

    with pytest.raises(ValueError, match="Missing required environment variables"):
        load_config()


def test_config_invalid_numeric(monkeypatch):
    """Test invalid numeric value."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")
    monkeypatch.setenv("SMA_FAST", "invalid")

    with pytest.raises(ValueError, match="Invalid numeric configuration value"):
        load_config()


def test_config_invalid_range(monkeypatch):
    """Test invalid range for MAX_POSITION_PCT."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")
    monkeypatch.setenv("MAX_POSITION_PCT", "1.5")

    with pytest.raises(ValueError, match="MAX_POSITION_PCT must be between 0 and 1"):
        load_config()


def test_config_sma_validation(monkeypatch):
    """Test SMA fast must be less than slow."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")
    monkeypatch.setenv("SMA_FAST", "30")
    monkeypatch.setenv("SMA_SLOW", "10")

    with pytest.raises(ValueError, match="SMA_FAST must be less than SMA_SLOW"):
        load_config()


def test_config_live_trading_enabled(monkeypatch):
    """Test live trading requires both flags."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")
    monkeypatch.setenv("ALPACA_PAPER", "false")
    monkeypatch.setenv("ALLOW_LIVE_TRADING", "true")

    config = load_config()

    assert config.is_live_trading_enabled() is True


def test_config_paper_trading_default(monkeypatch):
    """Test paper trading is default."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")

    config = load_config()

    assert config.is_live_trading_enabled() is False
    assert config.alpaca_paper is True


def test_config_kill_switch_env(monkeypatch):
    """Test kill switch via environment variable."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")
    monkeypatch.setenv("KILL_SWITCH", "true")

    config = load_config()

    assert config.kill_switch is True


def test_config_refuses_unsafe_live_configuration(monkeypatch):
    """Test that ALPACA_PAPER=false without ALLOW_LIVE_TRADING=true is rejected.

    This is a CRITICAL safety test. If ALPACA_PAPER=false but ALLOW_LIVE_TRADING=false,
    the bot could connect to the live API while the user thinks they're in paper mode.
    """
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")
    monkeypatch.setenv("ALPACA_PAPER", "false")
    monkeypatch.setenv("ALLOW_LIVE_TRADING", "false")

    with pytest.raises(ValueError, match="UNSAFE CONFIGURATION"):
        load_config()


def test_config_allows_explicit_live_trading(monkeypatch):
    """Test that explicit live trading configuration is allowed."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")
    monkeypatch.setenv("ALPACA_PAPER", "false")
    monkeypatch.setenv("ALLOW_LIVE_TRADING", "true")

    config = load_config()

    assert config.alpaca_paper is False
    assert config.allow_live_trading is True
    assert config.is_live_trading_enabled() is True


def test_config_kill_switch_file(monkeypatch):
    """Test kill switch via .kill_switch file."""
    monkeypatch.setenv("ALPACA_API_KEY", "test_key")
    monkeypatch.setenv("ALPACA_SECRET_KEY", "test_secret")

    # Create the actual kill switch file in the project root
    # (where config.py expects it to be)
    kill_switch_path = Path(__file__).parent.parent / ".kill_switch"

    try:
        # Create the file
        kill_switch_path.touch()

        config = load_config()
        assert config.kill_switch is True
    finally:
        # Always clean up, even if test fails
        if kill_switch_path.exists():
            kill_switch_path.unlink()
