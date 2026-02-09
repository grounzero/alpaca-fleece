from datetime import datetime, timezone

from src.position_tracker import PositionData, PositionTracker


class DummyBroker:
    pass


class DummyStateStore:
    def __init__(self):
        self.db_path = ":memory:"


def test_short_trailing_stop_moves_down():
    # Setup tracker with trailing stops enabled and custom trail pct
    tracker = PositionTracker(
        broker=DummyBroker(),
        state_store=DummyStateStore(),
        trailing_stop_enabled=True,
        trailing_stop_activation_pct=0.01,
        trailing_stop_trail_pct=0.02,
    )

    # Prevent DB persistence during test
    tracker._persist_position = lambda p: None

    # Start with a short position: entry 100, highest_price stores lowest observed price
    pos = PositionData(
        symbol="SHORT",
        side="short",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        highest_price=100.0,
        atr=None,
        trailing_stop_price=95.0,
        trailing_stop_activated=True,
    )

    tracker._positions["SHORT"] = pos

    # Price moves down to 90 -> for shorts we expect highest_price updated to 90
    # and trailing_stop lowered to current_price * (1 + trail_pct) = 90 * 1.02 = 91.8
    updated = tracker.update_current_price("SHORT", 90.0)
    assert updated is not None
    assert abs(updated.highest_price - 90.0) < 1e-6
    assert abs(updated.trailing_stop_price - (90.0 * 1.02)) < 1e-6
