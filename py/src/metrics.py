"""Metrics collection for bot monitoring.

Provides simple counters and gauges for tracking bot performance
without requiring log analysis.
"""

import json
import logging
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


@dataclass
class BotMetrics:
    """Runtime metrics for monitoring."""

    # Counters
    signals_generated: int = 0
    signals_filtered_confidence: int = 0
    signals_filtered_risk: int = 0
    orders_submitted: int = 0
    orders_filled: int = 0
    orders_rejected: int = 0
    exits_triggered: int = 0
    events_dropped: int = 0

    # Gauges
    open_positions: int = 0
    daily_pnl: float = 0.0
    daily_trade_count: int = 0

    # Timestamps
    last_signal_time: datetime = field(default_factory=lambda: datetime.now(timezone.utc))
    last_fill_time: datetime = field(default_factory=lambda: datetime.now(timezone.utc))
    started_at: datetime = field(default_factory=lambda: datetime.now(timezone.utc))

    def to_dict(self) -> dict[str, Any]:
        """Convert metrics to dictionary for JSON serialization."""
        return {
            "counters": {
                "signals_generated": self.signals_generated,
                "signals_filtered": self.signals_filtered_confidence + self.signals_filtered_risk,
                "signals_filtered_confidence": self.signals_filtered_confidence,
                "signals_filtered_risk": self.signals_filtered_risk,
                "orders_submitted": self.orders_submitted,
                "orders_filled": self.orders_filled,
                "orders_rejected": self.orders_rejected,
                "exits_triggered": self.exits_triggered,
                "events_dropped": self.events_dropped,
            },
            "gauges": {
                "open_positions": self.open_positions,
                "daily_pnl": round(self.daily_pnl, 2),
                "daily_trade_count": self.daily_trade_count,
            },
            "timestamps": {
                "last_signal_time": self.last_signal_time.isoformat(),
                "last_fill_time": self.last_fill_time.isoformat(),
                "started_at": self.started_at.isoformat(),
            },
            "uptime_seconds": int((datetime.now(timezone.utc) - self.started_at).total_seconds()),
        }

    def record_signal_generated(self) -> None:
        """Record that a signal was generated."""
        self.signals_generated += 1
        self.last_signal_time = datetime.now(timezone.utc)

    def record_signal_filtered_confidence(self) -> None:
        """Record that a signal was filtered due to low confidence."""
        self.signals_filtered_confidence += 1

    def record_signal_filtered_risk(self) -> None:
        """Record that a signal was filtered due to risk checks."""
        self.signals_filtered_risk += 1

    def record_order_submitted(self) -> None:
        """Record that an order was submitted."""
        self.orders_submitted += 1

    def record_order_filled(self) -> None:
        """Record that an order was filled."""
        self.orders_filled += 1
        self.last_fill_time = datetime.now(timezone.utc)

    def record_order_rejected(self) -> None:
        """Record that an order was rejected."""
        self.orders_rejected += 1

    def record_exit_triggered(self) -> None:
        """Record that an exit was triggered."""
        self.exits_triggered += 1

    def record_event_dropped(self) -> None:
        """Record that an event was dropped."""
        self.events_dropped += 1

    def update_open_positions(self, count: int) -> None:
        """Update the open positions gauge."""
        self.open_positions = count

    def update_daily_pnl(self, pnl: float) -> None:
        """Update the daily P&L gauge."""
        self.daily_pnl = pnl

    def update_daily_trade_count(self, count: int) -> None:
        """Update the daily trade count gauge."""
        self.daily_trade_count = count


# Global instance
metrics = BotMetrics()


def write_metrics_to_file(filepath: str | Path = "data/metrics.json") -> None:
    """Write current metrics to JSON file.

    Args:
        filepath: Path to write metrics file
    """
    path = Path(filepath)
    path.parent.mkdir(parents=True, exist_ok=True)

    try:
        with open(path, "w") as f:
            json.dump(metrics.to_dict(), f, indent=2)
    except Exception:
        logger.exception(f"Failed to write metrics file to {path}")
