"""Tests for Hybrid Trading (Crypto + Equities with extended hours support)."""

import pytest

from src.risk_manager import RiskManager
from src.strategy.sma_crossover import SMACrossover


@pytest.fixture
def risk_manager_hybrid(state_store, mock_broker, config):
    """Risk manager with hybrid (crypto + equities) configuration."""
    # Ensure config has hybrid setup
    hybrid_config = config.copy()
    hybrid_config["symbols"] = {
        "list": ["AAPL", "MSFT", "SPY", "BTCUSD", "ETHUSD"],
        "crypto_symbols": ["BTCUSD", "ETHUSD"],
    }
    hybrid_config["trading"] = {
        "session_policy": "include_extended",
        "shutdown_at_close": False,
    }
    hybrid_config["risk"] = {
        "regular_hours": {
            "max_position_pct": 0.10,
            "max_daily_loss_pct": 0.05,
            "max_trades_per_day": 20,
            "max_concurrent_positions": 10,
        },
        "extended_hours": {
            "max_position_pct": 0.05,
            "max_daily_loss_pct": 0.03,
            "max_trades_per_day": 10,
            "max_concurrent_positions": 5,
        },
    }

    return RiskManager(
        broker=mock_broker,
        data_handler=None,
        state_store=state_store,
        config=hybrid_config,
    )


class TestSessionDetection:
    """Test market session detection (regular vs extended hours)."""

    def test_crypto_detected_as_extended(self, risk_manager_hybrid):
        """Crypto symbols should use extended session limits."""
        session = risk_manager_hybrid._get_session_type("BTCUSD")
        assert session == "extended"

        session = risk_manager_hybrid._get_session_type("ETHUSD")
        assert session == "extended"

    def test_equity_detected_as_regular_or_extended(self, risk_manager_hybrid):
        """Equity symbols should use session-appropriate limits."""
        session = risk_manager_hybrid._get_session_type("AAPL")
        assert session in ["regular", "extended"]  # Depends on current time

        session = risk_manager_hybrid._get_session_type("SPY")
        assert session in ["regular", "extended"]

    def test_crypto_symbols_from_config(self, risk_manager_hybrid):
        """Risk manager should load crypto symbols from config."""
        assert "BTCUSD" in risk_manager_hybrid.crypto_symbols
        assert "ETHUSD" in risk_manager_hybrid.crypto_symbols


class TestSessionAwareLimits:
    """Test session-aware risk limits (Hybrid)."""

    def test_regular_limits_loaded(self, risk_manager_hybrid):
        """Regular hours limits should be loaded from config."""
        limits = risk_manager_hybrid.regular_limits

        assert limits.get("max_position_pct") == 0.10  # 10%
        assert limits.get("max_daily_loss_pct") == 0.05  # 5%
        assert limits.get("max_trades_per_day") == 20
        assert limits.get("max_concurrent_positions") == 10

    def test_extended_limits_loaded(self, risk_manager_hybrid):
        """Extended hours limits should be loaded from config."""
        limits = risk_manager_hybrid.extended_limits

        assert limits.get("max_position_pct") == 0.05  # 5% (tighter for crypto)
        assert limits.get("max_daily_loss_pct") == 0.03  # 3% (tighter for overnight)
        assert limits.get("max_trades_per_day") == 10
        assert limits.get("max_concurrent_positions") == 5

    def test_get_limits_for_crypto(self, risk_manager_hybrid):
        """Crypto should get extended limits."""
        limits = risk_manager_hybrid._get_limits("BTCUSD")

        assert limits.get("max_position_pct") == 0.05
        assert limits.get("max_daily_loss_pct") == 0.03

    def test_get_limits_for_equity(self, risk_manager_hybrid):
        """Equities should get session-appropriate limits."""
        limits = risk_manager_hybrid._get_limits("AAPL")

        # Should get either regular or extended depending on current time
        # But extended should be tighter
        assert limits.get("max_daily_loss_pct") >= 0.03  # At least extended tightness


class TestCryptoSymbolList:
    """Test crypto symbol configuration."""

    def test_crypto_symbols_in_config(self, config):
        """Crypto symbols should be specified in config."""
        symbols_config = config.get("symbols", {})
        crypto_symbols = symbols_config.get("crypto_symbols", [])

        # If crypto symbols exist, validate structure
        if crypto_symbols:
            assert len(crypto_symbols) >= 2
            assert "BTCUSD" in crypto_symbols or len(crypto_symbols) > 0

    def test_hybrid_symbol_list(self, config):
        """Symbol list should contain equities."""
        symbols_config = config.get("symbols", {})
        all_symbols = symbols_config.get("list", [])

        # Should have at least some symbols
        assert len(all_symbols) > 0
        assert "AAPL" in all_symbols  # Baseline equity should exist


class TestStrategyWithCrypto:
    """Test strategy compatibility with crypto symbols."""

    def test_strategy_accepts_crypto_symbols(self, state_store):
        """Strategy should accept crypto symbols parameter."""
        crypto_symbols = ["BTCUSD", "ETHUSD"]
<<<<<<< HEAD
        strategy = SMACrossover(state_store=state_store, crypto_symbols=crypto_symbols)
=======
        strategy = SMACrossover(crypto_symbols=crypto_symbols)
>>>>>>> 7e787d8 (Clean trading bot implementation)

        assert strategy.crypto_symbols == crypto_symbols

    def test_strategy_warmup_same_for_all(self, state_store):
        """Warmup period should be same for crypto and equities."""
        crypto_symbols = ["BTCUSD", "ETHUSD"]
<<<<<<< HEAD
        strategy = SMACrossover(state_store=state_store, crypto_symbols=crypto_symbols)
=======
        strategy = SMACrossover(crypto_symbols=crypto_symbols)
>>>>>>> 7e787d8 (Clean trading bot implementation)

        # All symbols need same warmup
        warmup_btc = strategy.get_required_history("BTCUSD")
        warmup_eth = strategy.get_required_history("ETHUSD")
        warmup_aapl = strategy.get_required_history("AAPL")

        assert warmup_btc == 51
        assert warmup_eth == 51
        assert warmup_aapl == 51


class TestSessionPolicy:
    """Test session policy configuration."""

    def test_session_policy_configured(self, config):
        """Session policy should be configured."""
        trading_config = config.get("trading", {})
        session_policy = trading_config.get("session_policy")

        # Should be one of these values
        assert session_policy in ["regular_only", "include_extended"]

    def test_shutdown_policy_configured(self, config):
        """Shutdown at close policy should be configured."""
        trading_config = config.get("trading", {})
        shutdown_at_close = trading_config.get("shutdown_at_close")

        # Should be boolean
        assert isinstance(shutdown_at_close, bool)


class TestLimitEnforcement:
    """Test that session-aware limits are enforced during risk checks."""

    def test_crypto_uses_tighter_loss_limit(self, risk_manager_hybrid):
        """Crypto should use tighter daily loss limit."""
        limits_btc = risk_manager_hybrid._get_limits("BTCUSD")
        limits_aapl = risk_manager_hybrid._get_limits("AAPL")

        # Extended limits should be tighter (if crypto)
        loss_btc = limits_btc.get("max_daily_loss_pct", 0.05)
        loss_aapl = limits_aapl.get("max_daily_loss_pct", 0.05)

        # Crypto limit should be <= equity limit (extended ≤ regular)
        assert loss_btc <= loss_aapl

    def test_crypto_uses_smaller_position_size(self, risk_manager_hybrid):
        """Crypto should use smaller position sizing."""
        limits_btc = risk_manager_hybrid._get_limits("BTCUSD")
        limits_aapl = risk_manager_hybrid._get_limits("AAPL")

        pos_btc = limits_btc.get("max_position_pct", 0.10)
        pos_aapl = limits_aapl.get("max_position_pct", 0.10)

        # Crypto position limit should be <= equity limit (extended ≤ regular)
        assert pos_btc <= pos_aapl


class TestBackwardCompatibility:
    """Test backward compatibility (old config format still works)."""

    def test_single_limit_format_supported(self, state_store, mock_broker, config_old_format):
        """Risk manager should support old single-limit format."""
        # Create risk manager with old-style config
        risk_manager = RiskManager(
            broker=mock_broker,
            data_handler=None,
            state_store=state_store,
            config=config_old_format,
        )

        # Should have limits (backward compatible)
        assert risk_manager.regular_limits is not None
        assert risk_manager.extended_limits is not None


@pytest.fixture
def config_old_format():
    """Old config format (single limits, not session-aware)."""
    return {
        "symbols": {
            "list": ["AAPL", "MSFT"],
            "crypto_symbols": [],
        },
        "trading": {
            "session_policy": "regular_only",
        },
        "risk": {
            "max_position_pct": 0.10,
            "max_daily_loss_pct": 0.05,
            "max_trades_per_day": 20,
            "max_concurrent_positions": 10,
        },
        "filters": {},
    }


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
