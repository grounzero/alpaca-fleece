"""Tests for P&L calculation for shorts (Fix 2.4)."""

from datetime import datetime, timezone
from unittest.mock import MagicMock

import pytest

from src.position_tracker import PositionData, PositionTracker


@pytest.fixture
def position_tracker(tmp_path):
    """Create position tracker with test database."""
    from src.broker import Broker
    from src.state_store import StateStore

    db_path = str(tmp_path / "test.db")
    state_store = StateStore(db_path)
    broker = MagicMock(spec=Broker)

    tracker = PositionTracker(broker, state_store)
    tracker.init_schema()
    return tracker


class TestPnLCalculation:
    """Test P&L calculation for both long and short positions."""

    def test_long_position_profit(self, position_tracker):
        """Long: entry=100, current=110 → pnl_pct=+0.10"""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=10,
            side="long",
        )

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 110.0)

        assert pnl_pct == pytest.approx(0.10)  # +10%
        assert pnl_amount == pytest.approx(100.0)  # 10 * (110 - 100)

    def test_long_position_loss(self, position_tracker):
        """Long: entry=100, current=90 → pnl_pct=-0.10"""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=10,
            side="long",
        )

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 90.0)

        assert pnl_pct == pytest.approx(-0.10)  # -10%
        assert pnl_amount == pytest.approx(-100.0)  # 10 * (90 - 100)

    def test_short_position_profit(self, position_tracker):
        """Short: entry=100, current=90 → pnl_pct=+0.10"""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=10,
            side="short",
        )

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 90.0)

        assert pnl_pct == pytest.approx(0.10)  # +10%
        assert pnl_amount == pytest.approx(100.0)  # 10 * (100 - 90)

    def test_short_position_loss(self, position_tracker):
        """Short: entry=100, current=110 → pnl_pct=-0.10"""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=10,
            side="short",
        )

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 110.0)

        assert pnl_pct == pytest.approx(-0.10)  # -10%
        assert pnl_amount == pytest.approx(-100.0)  # 10 * (100 - 110)

    def test_zero_entry_price_returns_zero(self, position_tracker):
        """Entry price of 0 returns (0.0, 0.0) without crash."""
        # Create position with zero entry price
        pos = PositionData(
            symbol="AAPL",
            side="long",
            qty=10,
            entry_price=0.0,
            entry_time=datetime.now(timezone.utc),
            extreme_price=0.0,
        )
        position_tracker._positions["AAPL"] = pos

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 100.0)

        assert pnl_amount == 0.0
        assert pnl_pct == 0.0

    def test_negative_entry_price_returns_zero(self, position_tracker):
        """Negative entry price returns (0.0, 0.0) without crash."""
        # Create position with negative entry price (shouldn't happen, but guard)
        pos = PositionData(
            symbol="AAPL",
            side="long",
            qty=10,
            entry_price=-100.0,
            entry_time=datetime.now(timezone.utc),
            extreme_price=0.0,
        )
        position_tracker._positions["AAPL"] = pos

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 100.0)

        assert pnl_amount == 0.0
        assert pnl_pct == 0.0

    def test_non_existent_position_returns_zero(self, position_tracker):
        """Non-existent position returns (0.0, 0.0)."""
        pnl_amount, pnl_pct = position_tracker.calculate_pnl("NONEXIST", 100.0)

        assert pnl_amount == 0.0
        assert pnl_pct == 0.0

    def test_long_breakeven_pnl_zero(self, position_tracker):
        """Long at breakeven: entry=100, current=100 → pnl_pct=0.0"""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=10,
            side="long",
        )

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 100.0)

        assert pnl_pct == pytest.approx(0.0)
        assert pnl_amount == pytest.approx(0.0)

    def test_short_breakeven_pnl_zero(self, position_tracker):
        """Short at breakeven: entry=100, current=100 → pnl_pct=0.0"""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=10,
            side="short",
        )

        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 100.0)

        assert pnl_pct == pytest.approx(0.0)
        assert pnl_amount == pytest.approx(0.0)

    def test_long_small_change_precision(self, position_tracker):
        """Long with small price changes maintains precision."""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=100,
            side="long",
        )

        # Price up $0.01
        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 100.01)

        assert pnl_pct == pytest.approx(0.0001)
        assert pnl_amount == pytest.approx(1.0)  # 100 shares * $0.01

    def test_short_small_change_precision(self, position_tracker):
        """Short with small price changes maintains precision."""
        position_tracker.start_tracking(
            symbol="AAPL",
            fill_price=100.0,
            qty=100,
            side="short",
        )

        # Price down $0.01
        pnl_amount, pnl_pct = position_tracker.calculate_pnl("AAPL", 99.99)

        assert pnl_pct == pytest.approx(0.0001)
        assert pnl_amount == pytest.approx(1.0)  # 100 shares * $0.01
