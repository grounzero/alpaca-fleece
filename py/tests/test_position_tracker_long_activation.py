from datetime import datetime, timezone

from src.position_tracker import PositionData, PositionTracker


class DummyBroker:
    pass


class DummyStateStore:
    def __init__(self):
        self.db_path = ":memory:"


def test_long_trailing_stop_activation():
    tracker = PositionTracker(
        broker=DummyBroker(),
        state_store=DummyStateStore(),
        trailing_stop_enabled=True,
        trailing_stop_activation_pct=0.02,
        trailing_stop_trail_pct=0.01,
    )

    # Avoid DB writes in tests
    tracker._persist_position = lambda p: None

    pos = PositionData(
        symbol="L",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=100.0,
        atr=None,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    tracker._positions["L"] = pos

    # Price rises to 103 -> unrealised_pct = (103-100)/100 = 0.03 >= 0.02 -> activate
    updated = tracker.update_current_price("L", 103.0)
    assert updated is not None
    assert updated.trailing_stop_activated is True
    # trailing stop should be set to current_price * (1 - trail_pct)
    assert abs(updated.trailing_stop_price - (103.0 * (1 - 0.01))) < 1e-6
