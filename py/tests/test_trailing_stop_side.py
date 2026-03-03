from datetime import datetime, timezone

from src.exit_manager import ExitManager
from src.position_tracker import PositionData


class Dummy:
    def calculate_pnl(self, symbol: str, current_price: float):
        # For tests we don't need realistic P&L, return zeros to avoid affecting logic
        return 0.0, 0.0


def make_exit_manager():
    # Provide minimal dummy dependencies; ExitManager stores these but does not call them in _evaluate_exit_rules
    return ExitManager(
        broker=Dummy(),
        position_tracker=Dummy(),
        event_bus=Dummy(),
        state_store=Dummy(),
        data_handler=Dummy(),
        trailing_stop_enabled=True,
    )


def test_trailing_stop_triggers_for_long():
    em = make_exit_manager()

    pos = PositionData(
        symbol="FOO",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=110.0,
        atr=None,
        trailing_stop_price=105.0,
        trailing_stop_activated=True,
    )

    # For long, trigger when current_price <= trailing_stop_price
    signal = em._evaluate_exit_rules(pos, current_price=104.0)
    assert signal is not None
    assert signal.reason == "trailing_stop"
    assert signal.side == "sell"


def test_trailing_stop_triggers_for_short():
    em = make_exit_manager()

    pos = PositionData(
        symbol="BAR",
        side="short",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=95.0,
        atr=None,
        trailing_stop_price=97.0,
        trailing_stop_activated=True,
    )

    # For short, trigger when current_price >= trailing_stop_price
    signal = em._evaluate_exit_rules(pos, current_price=98.0)
    assert signal is not None
    assert signal.reason == "trailing_stop"
    assert signal.side == "buy"
