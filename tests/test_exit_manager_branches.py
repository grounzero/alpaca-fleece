import asyncio
from datetime import datetime, timezone

from src.exit_manager import ExitManager
from src.position_tracker import PositionData


class DummyTracker:
    def __init__(self, pnl_map=None, positions=None):
        self.pnl_map = pnl_map or {}
        self._positions = positions or []

    def calculate_pnl(self, symbol: str, current_price: float):
        return self.pnl_map.get(symbol, (0.0, 0.0))

    def get_all_positions(self):
        return list(self._positions)


class DummyBroker:
    def get_clock(self):
        return {"is_open": True}


class DummyEventBus:
    def __init__(self):
        self.published = []

    async def publish(self, event):
        self.published.append(event)


class DummyStateStore:
    def get_state(self, k):
        return None


class DummyDataHandler:
    def get_snapshot(self, symbol):
        return {"last_price": 100.0}


def make_exit_manager(tracker):
    return ExitManager(
        broker=DummyBroker(),
        position_tracker=tracker,
        event_bus=DummyEventBus(),
        state_store=DummyStateStore(),
        data_handler=DummyDataHandler(),
        trailing_stop_enabled=False,
    )


def test_fallback_fixed_stop_triggers_when_no_atr():
    # Tracker reports a large negative pnl_pct for symbol 'Z'
    pnl_map = {"Z": (-5.0, -0.5)}
    tracker = DummyTracker(pnl_map=pnl_map)
    em = make_exit_manager(tracker)

    pos = PositionData(
        symbol="Z",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        highest_price=100.0,
        atr=None,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    sig = em._evaluate_exit_rules(pos, current_price=1.0)
    assert sig is not None and sig.reason == "stop_loss"


def test_close_all_positions_publishes_events():
    # Create one tracked position and ensure close_all_positions publishes one event
    pos = PositionData(
        symbol="Y",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        highest_price=100.0,
        atr=None,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    tracker = DummyTracker(positions=[pos])
    event_bus = DummyEventBus()
    em = ExitManager(
        broker=DummyBroker(),
        position_tracker=tracker,
        event_bus=event_bus,
        state_store=DummyStateStore(),
        data_handler=DummyDataHandler(),
    )

    # Run close_all_positions (async)
    asyncio.run(em.close_all_positions(reason="test"))
    assert len(event_bus.published) == 1
