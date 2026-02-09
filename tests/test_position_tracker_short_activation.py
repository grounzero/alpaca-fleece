from datetime import datetime, timezone

from src.position_tracker import PositionData, PositionTracker


class DummyBroker:
    pass


class DummyStateStore:
    def __init__(self):
        self.db_path = ":memory:"


def test_short_trailing_stop_activation():
    tracker = PositionTracker(
        broker=DummyBroker(),
        state_store=DummyStateStore(),
        trailing_stop_enabled=True,
        trailing_stop_activation_pct=0.01,
        trailing_stop_trail_pct=0.02,
    )

    # Avoid DB writes in tests
    tracker._persist_position = lambda p: None

    pos = PositionData(
        symbol="S",
        side="short",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        highest_price=100.0,
        atr=None,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    tracker._positions["S"] = pos

    # Current price drops to 98 -> unrealised_pct = (100 - 98) / 100 = 0.02 >= 0.01
    updated = tracker.update_current_price("S", 98.0)
    assert updated is not None
    assert updated.trailing_stop_activated is True
    # trailing_stop_price for shorts should be current_price * (1 + trail_pct)
    assert abs(updated.trailing_stop_price - (98.0 * 1.02)) < 1e-6
