"""Tests for metrics collection module."""

import json
from datetime import datetime, timedelta, timezone
from unittest.mock import patch

import pytest

from src.metrics import BotMetrics, metrics, write_metrics_to_file


@pytest.fixture(autouse=True)
def reset_global_metrics():
    """Reset global metrics singleton before each test to ensure isolation."""
    # Save original state
    original_state = {
        "signals_generated": metrics.signals_generated,
        "signals_filtered_confidence": metrics.signals_filtered_confidence,
        "signals_filtered_risk": metrics.signals_filtered_risk,
        "orders_submitted": metrics.orders_submitted,
        "orders_filled": metrics.orders_filled,
        "orders_rejected": metrics.orders_rejected,
        "exits_triggered": metrics.exits_triggered,
        "events_dropped": metrics.events_dropped,
        "open_positions": metrics.open_positions,
        "daily_pnl": metrics.daily_pnl,
        "daily_trade_count": metrics.daily_trade_count,
    }

    # Reset to zeros for test
    metrics.signals_generated = 0
    metrics.signals_filtered_confidence = 0
    metrics.signals_filtered_risk = 0
    metrics.orders_submitted = 0
    metrics.orders_filled = 0
    metrics.orders_rejected = 0
    metrics.exits_triggered = 0
    metrics.events_dropped = 0
    metrics.open_positions = 0
    metrics.daily_pnl = 0.0
    metrics.daily_trade_count = 0

    yield

    # Restore original state after test
    metrics.signals_generated = original_state["signals_generated"]
    metrics.signals_filtered_confidence = original_state["signals_filtered_confidence"]
    metrics.signals_filtered_risk = original_state["signals_filtered_risk"]
    metrics.orders_submitted = original_state["orders_submitted"]
    metrics.orders_filled = original_state["orders_filled"]
    metrics.orders_rejected = original_state["orders_rejected"]
    metrics.exits_triggered = original_state["exits_triggered"]
    metrics.events_dropped = original_state["events_dropped"]
    metrics.open_positions = original_state["open_positions"]
    metrics.daily_pnl = original_state["daily_pnl"]
    metrics.daily_trade_count = original_state["daily_trade_count"]


class TestBotMetrics:
    """Test BotMetrics dataclass."""

    def test_default_initialization(self):
        """Test that BotMetrics initializes with correct defaults."""
        m = BotMetrics()

        # Counters should be 0
        assert m.signals_generated == 0
        assert m.signals_filtered_confidence == 0
        assert m.signals_filtered_risk == 0
        assert m.orders_submitted == 0
        assert m.orders_filled == 0
        assert m.orders_rejected == 0
        assert m.exits_triggered == 0
        assert m.events_dropped == 0

        # Gauges should be at defaults
        assert m.open_positions == 0
        assert m.daily_pnl == 0.0
        assert m.daily_trade_count == 0

        # Timestamps should be set
        assert isinstance(m.last_signal_time, datetime)
        assert isinstance(m.last_fill_time, datetime)
        assert isinstance(m.started_at, datetime)

    def test_counters_increment(self):
        """Test that counter methods increment correctly."""
        m = BotMetrics()

        # Test signal counters
        m.record_signal_generated()
        assert m.signals_generated == 1

        m.record_signal_filtered_confidence()
        assert m.signals_filtered_confidence == 1

        m.record_signal_filtered_risk()
        assert m.signals_filtered_risk == 1

        # Test order counters
        m.record_order_submitted()
        assert m.orders_submitted == 1

        m.record_order_filled()
        assert m.orders_filled == 1

        m.record_order_rejected()
        assert m.orders_rejected == 1

        # Test exit and event counters
        m.record_exit_triggered()
        assert m.exits_triggered == 1

        m.record_event_dropped()
        assert m.events_dropped == 1

    def test_multiple_increments(self):
        """Test that counters can be incremented multiple times."""
        m = BotMetrics()

        for _ in range(5):
            m.record_signal_generated()

        for _ in range(3):
            m.record_order_filled()

        assert m.signals_generated == 5
        assert m.orders_filled == 3

    def test_gauge_updates(self):
        """Test that gauge update methods work correctly."""
        m = BotMetrics()

        m.update_open_positions(5)
        assert m.open_positions == 5

        m.update_daily_pnl(1234.56)
        assert m.daily_pnl == 1234.56

        m.update_daily_trade_count(10)
        assert m.daily_trade_count == 10

    def test_to_dict_structure(self):
        """Test that to_dict returns expected structure."""
        m = BotMetrics()
        m.record_signal_generated()
        m.record_order_filled()
        m.update_open_positions(3)

        result = m.to_dict()

        # Check top-level keys
        assert "counters" in result
        assert "gauges" in result
        assert "timestamps" in result
        assert "uptime_seconds" in result

        # Check counters
        counters = result["counters"]
        assert "signals_generated" in counters
        assert "signals_filtered" in counters
        assert "signals_filtered_confidence" in counters
        assert "signals_filtered_risk" in counters
        assert "orders_submitted" in counters
        assert "orders_filled" in counters
        assert "orders_rejected" in counters
        assert "exits_triggered" in counters
        assert "events_dropped" in counters

        # Check gauges
        gauges = result["gauges"]
        assert "open_positions" in gauges
        assert "daily_pnl" in gauges
        assert "daily_trade_count" in gauges

        # Check timestamps
        timestamps = result["timestamps"]
        assert "last_signal_time" in timestamps
        assert "last_fill_time" in timestamps
        assert "started_at" in timestamps

    def test_to_dict_values(self):
        """Test that to_dict returns correct values."""
        m = BotMetrics()
        m.record_signal_generated()
        m.record_signal_generated()
        m.record_signal_filtered_confidence()
        m.record_signal_filtered_risk()
        m.record_order_filled()
        m.update_open_positions(2)
        m.update_daily_pnl(100.50)

        result = m.to_dict()

        assert result["counters"]["signals_generated"] == 2
        assert result["counters"]["signals_filtered_confidence"] == 1
        assert result["counters"]["signals_filtered_risk"] == 1
        # signals_filtered is the sum of confidence + risk
        assert result["counters"]["signals_filtered"] == 2
        assert result["counters"]["orders_filled"] == 1
        assert result["gauges"]["open_positions"] == 2
        assert result["gauges"]["daily_pnl"] == 100.50

    def test_to_dict_timestamp_format(self):
        """Test that timestamps are formatted as ISO strings."""
        m = BotMetrics()
        result = m.to_dict()

        # Timestamps should be ISO format strings
        for ts_key in ["last_signal_time", "last_fill_time", "started_at"]:
            ts_value = result["timestamps"][ts_key]
            # Should be able to parse it back
            parsed = datetime.fromisoformat(ts_value)
            assert isinstance(parsed, datetime)

    def test_to_dict_daily_pnl_rounding(self):
        """Test that daily_pnl is rounded to 2 decimal places."""
        m = BotMetrics()
        m.update_daily_pnl(123.456789)

        result = m.to_dict()
        assert result["gauges"]["daily_pnl"] == 123.46

    def test_uptime_seconds(self):
        """Test that uptime_seconds is calculated correctly."""
        # Create metrics with started_at 5 seconds ago
        past_time = datetime.now(timezone.utc) - timedelta(seconds=5)
        m = BotMetrics(started_at=past_time)

        result = m.to_dict()
        # Uptime should be at least 5 seconds
        assert result["uptime_seconds"] >= 5
        assert isinstance(result["uptime_seconds"], int)

    def test_signal_timestamp_updated(self):
        """Test that record_signal_generated updates last_signal_time."""
        m = BotMetrics()
        old_time = m.last_signal_time
        future_time = old_time + timedelta(seconds=1)

        # Patch datetime.now in the metrics module
        with patch("src.metrics.datetime") as mock_datetime:
            mock_datetime.now.return_value = future_time
            mock_datetime.timezone = timezone
            m.record_signal_generated()

        assert m.last_signal_time == future_time

    def test_fill_timestamp_updated(self):
        """Test that record_order_filled updates last_fill_time."""
        m = BotMetrics()
        old_time = m.last_fill_time
        future_time = old_time + timedelta(seconds=1)

        # Patch datetime.now in the metrics module
        with patch("src.metrics.datetime") as mock_datetime:
            mock_datetime.now.return_value = future_time
            mock_datetime.timezone = timezone
            m.record_order_filled()

        assert m.last_fill_time == future_time


class TestWriteMetricsToFile:
    """Test metrics file writing functionality."""

    def test_writes_valid_json(self, tmp_path):
        """Test that write_metrics_to_file creates valid JSON."""
        filepath = tmp_path / "metrics.json"

        # Reset metrics to known state
        metrics.signals_generated = 5
        metrics.orders_filled = 3

        write_metrics_to_file(str(filepath))

        assert filepath.exists()

        with open(filepath) as f:
            data = json.load(f)

        assert data["counters"]["signals_generated"] == 5
        assert data["counters"]["orders_filled"] == 3

    def test_creates_parent_directories(self, tmp_path):
        """Test that write_metrics_to_file creates parent directories."""
        filepath = tmp_path / "subdir" / "nested" / "metrics.json"

        write_metrics_to_file(str(filepath))

        assert filepath.exists()

    def test_overwrites_existing_file(self, tmp_path):
        """Test that write_metrics_to_file overwrites existing file."""
        filepath = tmp_path / "metrics.json"

        # Write initial data
        with open(filepath, "w") as f:
            json.dump({"old": "data"}, f)

        # Reset and write new metrics
        metrics.signals_generated = 10
        write_metrics_to_file(str(filepath))

        with open(filepath) as f:
            data = json.load(f)

        assert "old" not in data
        assert "counters" in data
        assert data["counters"]["signals_generated"] == 10


class TestGlobalMetricsInstance:
    """Test the global metrics instance."""

    def test_global_instance_exists(self):
        """Test that global metrics instance exists."""
        assert isinstance(metrics, BotMetrics)

    def test_global_instance_is_singleton(self):
        """Test that global metrics is a shared instance."""
        from src.metrics import metrics as metrics2

        assert metrics is metrics2

    def test_global_instance_is_same_object(self):
        """Test that global metrics is the same object in memory."""
        from src.metrics import metrics as m1
        from src.metrics import metrics as m2

        # Both should reference the same object
        assert m1 is m2

        # Modifying one should affect the other
        original = m1.signals_generated
        m1.record_signal_generated()
        assert m2.signals_generated == original + 1
