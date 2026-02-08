from datetime import datetime, timezone

from src.exit_manager import ExitManager, calculate_dynamic_stops
from src.position_tracker import PositionData


class DummyTracker:
    def calculate_pnl(self, symbol: str, current_price: float):
        return 0.0, 0.0


class Dummy:  # generic dummy for other deps
    pass


def make_exit_manager():
    return ExitManager(
        broker=Dummy(),
        position_tracker=DummyTracker(),
        event_bus=Dummy(),
        state_store=Dummy(),
        data_handler=Dummy(),
        trailing_stop_enabled=False,
    )


def test_calculate_dynamic_stops_invalid_atr():
    # NaN atr should raise ValueError
    try:
        calculate_dynamic_stops(100.0, atr=float("nan"), side="long")
        assert False, "Expected ValueError for NaN atr"
    except ValueError:
        pass

    # Infinite atr should raise ValueError
    try:
        calculate_dynamic_stops(100.0, atr=float("inf"), side="long")
        assert False, "Expected ValueError for infinite atr"
    except ValueError:
        pass


def test_calculate_dynamic_stops_invalid_multiplier():
    # Negative multiplier should raise ValueError
    try:
        calculate_dynamic_stops(100.0, atr=1.0, atr_multiplier_stop=-1.0, side="long")
        assert False, "Expected ValueError for negative multiplier"
    except ValueError:
        pass


def test_atr_nan_skips_atr_logic_and_uses_fallback():
    # When position.atr is NaN, ATR logic must be skipped and fallback fixed stop applied
    # Use a tracker that reports a large negative pnl_pct so fallback fixed stop applies
    class BadPnlTracker(DummyTracker):
        def calculate_pnl(self, symbol: str, current_price: float):
            return -100.0, -0.9

    em = ExitManager(
        broker=Dummy(),
        position_tracker=BadPnlTracker(),
        event_bus=Dummy(),
        state_store=Dummy(),
        data_handler=Dummy(),
        trailing_stop_enabled=False,
    )

    pos = PositionData(
        symbol="N",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        highest_price=100.0,
        atr=float("nan"),
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    # Simulate a very negative P&L by passing a low current_price that would trigger fallback stop
    sig = em._evaluate_exit_rules(pos, current_price=1.0)
    assert sig is not None and sig.reason == "stop_loss"
