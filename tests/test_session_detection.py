"""Tests for session detection math fix (Fix 2.2)."""

from datetime import datetime
from unittest.mock import MagicMock, patch
from zoneinfo import ZoneInfo

import pytest

from src.risk_manager import RiskManager


@pytest.fixture
def risk_manager_config():
    """Provide test config."""
    return {
        "risk": {
            "regular_hours": {
                "max_position_pct": 0.10,
                "max_daily_loss_pct": 0.05,
                "max_trades_per_day": 20,
                "max_concurrent_positions": 10,
            },
            "extended_hours": {
                "max_position_pct": 0.20,
                "max_daily_loss_pct": 0.10,
                "max_trades_per_day": 40,
                "max_concurrent_positions": 20,
            },
        },
        "symbols": {"crypto_symbols": ["BTCUSD", "ETHUSD"]},
        "filters": {},
    }


@pytest.fixture
def risk_manager(risk_manager_config):
    """Create risk manager for testing."""
    broker = MagicMock()
    data_handler = MagicMock()
    state_store = MagicMock()

    return RiskManager(
        broker=broker,
        data_handler=data_handler,
        state_store=state_store,
        config=risk_manager_config,
    )


class TestSessionDetection:
    """Test session type detection with correct time handling."""

    def test_crypto_symbol_returns_extended_hours(self, risk_manager):
        """Crypto symbols always return 'extended' session."""
        session = risk_manager._get_session_type("BTCUSD")
        assert session == "extended"

        session = risk_manager._get_session_type("ETHUSD")
        assert session == "extended"

    def test_regular_equity_at_930am_returns_regular(self, risk_manager):
        """Equity at 9:30 AM ET returns 'regular' session."""
        # Mock current time: 9:30 AM ET
        mock_time = datetime(2025, 2, 10, 9, 30, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            session = risk_manager._get_session_type("AAPL")
            assert session == "regular"

    def test_regular_equity_at_900am_returns_extended(self, risk_manager):
        """Equity at 9:00 AM ET (before market open) returns 'extended' session."""
        # Mock current time: 9:00 AM ET
        mock_time = datetime(2025, 2, 10, 9, 0, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            session = risk_manager._get_session_type("AAPL")
            assert session == "extended"

    def test_regular_equity_at_929am_returns_extended(self, risk_manager):
        """Equity at 9:29 AM ET (before market open) returns 'extended' session."""
        # Mock current time: 9:29 AM ET
        mock_time = datetime(2025, 2, 10, 9, 29, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            session = risk_manager._get_session_type("AAPL")
            assert session == "extended"

    def test_regular_equity_at_359pm_returns_regular(self, risk_manager):
        """Equity at 3:59 PM ET (before market close) returns 'regular' session."""
        # Mock current time: 3:59 PM ET (15:59)
        mock_time = datetime(2025, 2, 10, 15, 59, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            session = risk_manager._get_session_type("AAPL")
            assert session == "regular"

    def test_regular_equity_at_400pm_returns_extended(self, risk_manager):
        """Equity at 4:00 PM ET (market close) returns 'extended' session."""
        # Mock current time: 4:00 PM ET (16:00)
        mock_time = datetime(2025, 2, 10, 16, 0, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            session = risk_manager._get_session_type("AAPL")
            assert session == "extended"

    def test_regular_equity_at_noon_returns_regular(self, risk_manager):
        """Equity at noon ET returns 'regular' session."""
        # Mock current time: 12:00 PM ET
        mock_time = datetime(2025, 2, 10, 12, 0, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            session = risk_manager._get_session_type("AAPL")
            assert session == "regular"

    def test_handles_minutes_correctly(self, risk_manager):
        """Session detection handles minutes correctly (not just hours)."""
        # 9:30:30 AM ET should still be regular (comparing datetimes, not doubles)
        mock_time = datetime(2025, 2, 10, 9, 30, 30, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            session = risk_manager._get_session_type("MSFT")
            assert session == "regular"


class TestSessionBasedLimits:
    """Test that risk limits vary by session."""

    def test_get_limits_returns_regular_limits_during_market_hours(self, risk_manager):
        """_get_limits returns regular limits during market hours."""
        mock_time = datetime(2025, 2, 10, 14, 0, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            limits = risk_manager._get_limits("AAPL")

            assert limits["max_concurrent_positions"] == 10  # Regular limit
            assert limits["max_daily_loss_pct"] == 0.05

    def test_get_limits_returns_extended_limits_after_hours(self, risk_manager):
        """_get_limits returns extended limits after market hours."""
        mock_time = datetime(2025, 2, 10, 18, 0, 0, tzinfo=ZoneInfo("America/New_York"))

        with patch("src.risk_manager.datetime") as mock_datetime:
            mock_datetime.now.return_value = mock_time
            mock_datetime.side_effect = lambda *args, **kwargs: datetime(*args, **kwargs)

            limits = risk_manager._get_limits("AAPL")

            assert limits["max_concurrent_positions"] == 20  # Extended limit
            assert limits["max_daily_loss_pct"] == 0.10
