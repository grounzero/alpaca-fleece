from datetime import datetime, timezone

from src.exit_manager import ExitManager, calculate_dynamic_stops
from src.position_tracker import PositionData


class DummyTracker:
    def __init__(self, pnl_map=None):
        # pnl_map: symbol -> (pnl_amount, pnl_pct)
        self.pnl_map = pnl_map or {}

    def calculate_pnl(self, symbol: str, current_price: float):
        return self.pnl_map.get(symbol, (0.0, 0.0))


class Dummy:  # generic dummy for other deps
    pass


def make_exit_manager(pnl_map=None):
    tracker = DummyTracker(pnl_map=pnl_map)
    return ExitManager(
        broker=Dummy(),
        position_tracker=tracker,
        event_bus=Dummy(),
        state_store=Dummy(),
        data_handler=Dummy(),
        trailing_stop_enabled=False,
    )


def test_atr_dynamic_stop_long_triggers_stop_and_target():
    em = make_exit_manager()

    pos = PositionData(
        symbol="A",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=100.0,
        atr=2.0,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    stop_price, target_price = calculate_dynamic_stops(
        100.0, atr=2.0, atr_multiplier_stop=1.5, atr_multiplier_target=3.0, side="long"
    )

    # Price below stop -> stop_loss
    sig = em._evaluate_exit_rules(pos, current_price=stop_price - 0.1)
    assert sig is not None and sig.reason == "stop_loss" and sig.side == "sell"

    # Price above target -> profit_target
    sig = em._evaluate_exit_rules(pos, current_price=target_price + 0.1)
    assert sig is not None and sig.reason == "profit_target" and sig.side == "sell"


def test_atr_dynamic_stop_short_triggers_stop_and_target():
    em = make_exit_manager()

    pos = PositionData(
        symbol="B",
        side="short",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=100.0,
        atr=1.0,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    stop_price, target_price = calculate_dynamic_stops(
        100.0, atr=1.0, atr_multiplier_stop=2.0, atr_multiplier_target=3.0, side="short"
    )

    # For short, stop triggers when current_price >= stop_price
    sig = em._evaluate_exit_rules(pos, current_price=stop_price + 0.1)
    assert sig is not None and sig.reason == "stop_loss" and sig.side == "buy"

    # For short, target triggers when current_price <= target_price
    sig = em._evaluate_exit_rules(pos, current_price=target_price - 0.1)
    assert sig is not None and sig.reason == "profit_target" and sig.side == "buy"


def test_atr_precedence_skips_fallback_fixed_pct():
    # If ATR thresholds are computed, fallback fixed-percentage stop should not fire
    # even when pnl_pct exceeds stop_loss_pct.
    # Configure DummyTracker to report large negative pnl_pct for symbol 'C'
    pnl_map = {"C": (-10.0, -0.5)}  # -50% P&L
    em = make_exit_manager(pnl_map=pnl_map)

    pos = PositionData(
        symbol="C",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=100.0,
        atr=5.0,  # ATR present so atr_computed will be True
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    # Provide current price that would normally trigger fixed stop (via pnl), but ATR stop is not breached
    # ATR-based stop will be entry_price - 5*1.5 = 100 - 7.5 = 92.5; use price above that to avoid ATR stop
    sig = em._evaluate_exit_rules(pos, current_price=95.0)
    # Because ATR is present and not breached, we expect no signal despite large pnl_map value
    assert sig is None
