from datetime import datetime, timezone

from src.exit_manager import calculate_dynamic_stops, ExitManager
from src.position_tracker import PositionData


def test_calculate_dynamic_stops_long_short() -> None:
    stop, target = calculate_dynamic_stops(entry_price=100.0, atr=2.0)
    assert stop == 97.0
    assert target == 106.0

    stop_s, target_s = calculate_dynamic_stops(entry_price=100.0, atr=2.0, side="short")
    assert stop_s == 103.0
    assert target_s == 94.0


class DummyTracker:
    def __init__(self, entry_price: float, qty: float) -> None:
        self.entry_price = entry_price
        self.qty = qty

    def calculate_pnl(self, symbol: str, current_price: float):
        pnl_amount = (current_price - self.entry_price) * self.qty
        pnl_pct = (current_price - self.entry_price) / self.entry_price
        return pnl_amount, pnl_pct


def test_evaluate_exit_rules_atr_long_stop() -> None:
    entry = 100.0
    atr = 2.0
    qty = 1.0
    now = datetime.now(timezone.utc)

    position = PositionData(
        symbol="AAPL",
        side="long",
        qty=qty,
        entry_price=entry,
        entry_time=now,
        highest_price=entry,
        atr=atr,
    )

    tracker = DummyTracker(entry_price=entry, qty=qty)
    mgr = ExitManager(broker=None, position_tracker=tracker, event_bus=None, state_store=None, data_handler=None)

    # ATR-based stop for long: stop = entry - atr*1.5 = 97.0 -> price 96 triggers stop
    sig = mgr._evaluate_exit_rules(position, current_price=96.0)
    assert sig is not None
    assert sig.reason == "stop_loss"
    assert sig.side == "sell"


def test_evaluate_exit_rules_fallback_stop_when_atr_missing() -> None:
    entry = 100.0
    qty = 1.0
    now = datetime.now(timezone.utc)

    position = PositionData(
        symbol="AAPL",
        side="long",
        qty=qty,
        entry_price=entry,
        entry_time=now,
        highest_price=entry,
        atr=None,
    )

    tracker = DummyTracker(entry_price=entry, qty=qty)
    mgr = ExitManager(broker=None, position_tracker=tracker, event_bus=None, state_store=None, data_handler=None)

    # No ATR available: fallback to percent stop (default 1%). Price 98 -> -2% triggers stop
    sig = mgr._evaluate_exit_rules(position, current_price=98.0)
    assert sig is not None
    assert sig.reason == "stop_loss"
    assert sig.side == "sell"
