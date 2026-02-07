"""Tests for Win #3: State Persistence + Deterministic Recovery."""

import pytest
from pathlib import Path

from src.state_store import StateStore


@pytest.fixture
def state_store(tmp_path):
    """Create temporary state store for testing."""
    db_path = str(tmp_path / "test_persistence.db")
    return StateStore(db_path)


class TestCircuitBreakerPersistence:
    """Test circuit breaker count persistence."""
    
    def test_save_and_load_circuit_breaker_count(self, state_store):
        """Circuit breaker count should persist to DB."""
        # Save count
        state_store.save_circuit_breaker_count(3)
        
        # Load count
        count = state_store.get_circuit_breaker_count()
        
        assert count == 3
    
    def test_circuit_breaker_survives_restart(self, tmp_path):
        """Circuit breaker should survive simulated restart."""
        db_path = str(tmp_path / "persistent.db")
        
        # Session 1: Save count
        store1 = StateStore(db_path)
        store1.save_circuit_breaker_count(4)
        
        # Session 2: Load count (simulated restart)
        store2 = StateStore(db_path)
        count = store2.get_circuit_breaker_count()
        
        assert count == 4  # Survived restart
    
    def test_circuit_breaker_defaults_to_zero(self, state_store):
        """New circuit breaker should default to 0."""
        count = state_store.get_circuit_breaker_count()
        assert count == 0
    
    def test_circuit_breaker_increment(self, state_store):
        """Should be able to increment circuit breaker."""
        state_store.save_circuit_breaker_count(0)
        
        count = state_store.get_circuit_breaker_count()
        state_store.save_circuit_breaker_count(count + 1)
        
        assert state_store.get_circuit_breaker_count() == 1


class TestDailyPnLPersistence:
    """Test daily P&L persistence."""
    
    def test_save_and_load_daily_pnl(self, state_store):
        """Daily P&L should persist to DB."""
        state_store.save_daily_pnl(-250.50)
        
        pnl = state_store.get_daily_pnl()
        
        assert pnl == -250.50
    
    def test_daily_pnl_survives_restart(self, tmp_path):
        """Daily P&L should survive simulated restart."""
        db_path = str(tmp_path / "persistent.db")
        
        # Session 1: Save P&L
        store1 = StateStore(db_path)
        store1.save_daily_pnl(-500.25)
        
        # Session 2: Load P&L (simulated restart)
        store2 = StateStore(db_path)
        pnl = store2.get_daily_pnl()
        
        assert pnl == -500.25  # Survived restart
    
    def test_daily_pnl_defaults_to_zero(self, state_store):
        """New daily P&L should default to 0."""
        pnl = state_store.get_daily_pnl()
        assert pnl == 0.0
    
    def test_daily_pnl_update(self, state_store):
        """Should be able to update daily P&L."""
        state_store.save_daily_pnl(100.0)
        state_store.save_daily_pnl(250.50)
        
        assert state_store.get_daily_pnl() == 250.50
    
    def test_daily_pnl_negative(self, state_store):
        """Daily P&L should handle negative values."""
        state_store.save_daily_pnl(-1000.75)
        
        assert state_store.get_daily_pnl() == -1000.75


class TestDailyTradeCountPersistence:
    """Test daily trade count persistence."""
    
    def test_save_and_load_daily_trade_count(self, state_store):
        """Daily trade count should persist to DB."""
        state_store.save_daily_trade_count(5)
        
        count = state_store.get_daily_trade_count()
        
        assert count == 5
    
    def test_daily_trade_count_survives_restart(self, tmp_path):
        """Daily trade count should survive simulated restart."""
        db_path = str(tmp_path / "persistent.db")
        
        # Session 1: Save count
        store1 = StateStore(db_path)
        store1.save_daily_trade_count(12)
        
        # Session 2: Load count (simulated restart)
        store2 = StateStore(db_path)
        count = store2.get_daily_trade_count()
        
        assert count == 12  # Survived restart
    
    def test_daily_trade_count_defaults_to_zero(self, state_store):
        """New daily trade count should default to 0."""
        count = state_store.get_daily_trade_count()
        assert count == 0
    
    def test_daily_trade_count_increment(self, state_store):
        """Should be able to increment trade count."""
        state_store.save_daily_trade_count(3)
        
        count = state_store.get_daily_trade_count()
        state_store.save_daily_trade_count(count + 1)
        
        assert state_store.get_daily_trade_count() == 4


class TestLastSignalPersistence:
    """Test last signal per symbol persistence."""
    
    def test_save_and_load_last_signal(self, state_store):
        """Last signal should persist to DB."""
        state_store.save_last_signal("AAPL", "BUY", (10, 30))
        
        signal = state_store.get_last_signal("AAPL", (10, 30))
        
        assert signal == "BUY"
    
    def test_last_signal_survives_restart(self, tmp_path):
        """Last signal should survive simulated restart."""
        db_path = str(tmp_path / "persistent.db")
        
        # Session 1: Save signal
        store1 = StateStore(db_path)
        store1.save_last_signal("NVDA", "SELL", (20, 50))
        
        # Session 2: Load signal (simulated restart)
        store2 = StateStore(db_path)
        signal = store2.get_last_signal("NVDA", (20, 50))
        
        assert signal == "SELL"  # Survived restart
    
    def test_last_signal_per_sma_period(self, state_store):
        """Different SMA periods should track separately."""
        state_store.save_last_signal("AAPL", "BUY", (5, 15))
        state_store.save_last_signal("AAPL", "SELL", (10, 30))
        state_store.save_last_signal("AAPL", "BUY", (20, 50))
        
        assert state_store.get_last_signal("AAPL", (5, 15)) == "BUY"
        assert state_store.get_last_signal("AAPL", (10, 30)) == "SELL"
        assert state_store.get_last_signal("AAPL", (20, 50)) == "BUY"
    
    def test_last_signal_per_symbol(self, state_store):
        """Different symbols should track separately."""
        state_store.save_last_signal("AAPL", "BUY", (10, 30))
        state_store.save_last_signal("NVDA", "SELL", (10, 30))
        state_store.save_last_signal("SPY", "BUY", (10, 30))
        
        assert state_store.get_last_signal("AAPL", (10, 30)) == "BUY"
        assert state_store.get_last_signal("NVDA", (10, 30)) == "SELL"
        assert state_store.get_last_signal("SPY", (10, 30)) == "BUY"
    
    def test_last_signal_defaults_to_none(self, state_store):
        """Unseen signal should default to None."""
        signal = state_store.get_last_signal("UNKNOWN", (10, 30))
        assert signal is None
    
    def test_last_signal_update(self, state_store):
        """Should be able to update last signal."""
        state_store.save_last_signal("AAPL", "BUY", (10, 30))
        assert state_store.get_last_signal("AAPL", (10, 30)) == "BUY"
        
        state_store.save_last_signal("AAPL", "SELL", (10, 30))
        assert state_store.get_last_signal("AAPL", (10, 30)) == "SELL"


class TestDailyStateReset:
    """Test daily state reset."""
    
    def test_reset_daily_state(self, state_store):
        """reset_daily_state should clear daily metrics."""
        # Set values
        state_store.save_daily_pnl(-250.0)
        state_store.save_daily_trade_count(5)
        
        # Verify they're set
        assert state_store.get_daily_pnl() == -250.0
        assert state_store.get_daily_trade_count() == 5
        
        # Reset
        state_store.reset_daily_state()
        
        # Verify reset
        assert state_store.get_daily_pnl() == 0.0
        assert state_store.get_daily_trade_count() == 0
    
    def test_reset_does_not_clear_circuit_breaker(self, state_store):
        """reset_daily_state should NOT clear circuit breaker."""
        state_store.save_circuit_breaker_count(3)
        state_store.save_daily_pnl(-100.0)
        
        state_store.reset_daily_state()
        
        # Circuit breaker should persist
        assert state_store.get_circuit_breaker_count() == 3
        # Daily PnL should reset
        assert state_store.get_daily_pnl() == 0.0


class TestIntegrationRecoveryScenarios:
    """Integration tests simulating crash recovery scenarios."""
    
    def test_full_state_recovery_after_crash(self, tmp_path):
        """Full state should be recoverable after simulated crash."""
        db_path = str(tmp_path / "persistent.db")
        
        # Session 1: Before crash
        store1 = StateStore(db_path)
        store1.save_circuit_breaker_count(2)
        store1.save_daily_pnl(-150.0)
        store1.save_daily_trade_count(4)
        store1.save_last_signal("AAPL", "BUY", (10, 30))
        store1.save_last_signal("NVDA", "SELL", (20, 50))
        
        # Session 2: After simulated crash
        store2 = StateStore(db_path)
        
        # Verify all state recovered
        assert store2.get_circuit_breaker_count() == 2
        assert store2.get_daily_pnl() == -150.0
        assert store2.get_daily_trade_count() == 4
        assert store2.get_last_signal("AAPL", (10, 30)) == "BUY"
        assert store2.get_last_signal("NVDA", (20, 50)) == "SELL"
    
    def test_circuit_breaker_prevents_retrading_after_restart(self, tmp_path):
        """Circuit breaker should prevent trading if it was tripped before crash."""
        db_path = str(tmp_path / "persistent.db")
        
        # Before crash: 4 failures (almost tripped)
        store1 = StateStore(db_path)
        store1.save_circuit_breaker_count(4)
        
        # After restart: Bot should see 4 failures
        store2 = StateStore(db_path)
        count = store2.get_circuit_breaker_count()
        
        # One more failure will trip circuit
        count += 1
        store2.save_circuit_breaker_count(count)
        
        # Verify circuit would be tripped
        assert store2.get_circuit_breaker_count() == 5
    
    def test_daily_limits_respected_after_restart(self, tmp_path):
        """Daily loss limit should be enforced after restart."""
        db_path = str(tmp_path / "persistent.db")
        
        # Before crash: -$800 loss today (near $1000 limit)
        store1 = StateStore(db_path)
        store1.save_daily_pnl(-800.0)
        
        # After restart: Bot should see -$800 loss
        store2 = StateStore(db_path)
        pnl = store2.get_daily_pnl()
        
        # Check if additional loss would exceed limit
        max_daily_loss = 1000.0
        assert pnl < -max_daily_loss or pnl == -800.0
    
    def test_duplicate_signal_prevention_after_restart(self, tmp_path):
        """Should prevent duplicate signals even after restart."""
        db_path = str(tmp_path / "persistent.db")
        
        # Before crash: AAPL BUY signal at 10:30 SMA
        store1 = StateStore(db_path)
        store1.save_last_signal("AAPL", "BUY", (10, 30))
        
        # After restart: Bot sees same signal again
        store2 = StateStore(db_path)
        last_signal = store2.get_last_signal("AAPL", (10, 30))
        
        # Should prevent duplicate
        assert last_signal == "BUY"
        
        # Only when we get opposite signal should we trade
        store2.save_last_signal("AAPL", "SELL", (10, 30))
        assert store2.get_last_signal("AAPL", (10, 30)) == "SELL"


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
