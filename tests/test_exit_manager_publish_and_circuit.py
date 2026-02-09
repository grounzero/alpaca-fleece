import asyncio
from datetime import datetime, timezone

import pytest

from src.exit_manager import ExitManager
from src.position_tracker import PositionData


class DummyBroker:
    pass


class DummyEventBus:
    def __init__(self, raise_on_publish=False):
        self.published = []
        self.raise_on_publish = raise_on_publish

    async def publish(self, event):
        if self.raise_on_publish:
            raise RuntimeError("publish failure")
        self.published.append(event)


class DummyStateStore:
    def __init__(self, cb_state=None):
        self._cb = cb_state

    def get_state(self, k):
        if k == "circuit_breaker_state":
            return self._cb
        return None


class DummyDataHandler:
    def __init__(self, price_map=None):
        self.price_map = price_map or {}

    def get_snapshot(self, symbol):
        p = self.price_map.get(symbol)
        if p is None:
            return None
        return {"last_price": p}


class DummyTracker:
    def __init__(self, positions=None, pnl_map=None):
        self._positions = positions or []
        self._pnl_map = pnl_map or {}

    def get_all_positions(self):
        return list(self._positions)

    def calculate_pnl(self, symbol, current_price):
        return self._pnl_map.get(symbol, (0.0, 0.0))


def make_em(tracker, event_bus, state_store, data_handler, **kwargs):
    return ExitManager(
        broker=DummyBroker(),
        position_tracker=tracker,
        event_bus=event_bus,
        state_store=state_store,
        data_handler=data_handler,
        **kwargs,
    )


def test_atr_precedence_skips_fallback():
    # ATR present and valid; ensure ATR thresholds take precedence over fixed pct fallback
    pos = PositionData(
        symbol="C",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=100.0,
        atr=2.0,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    # Tracker reports huge negative pnl_pct so fallback would trigger if ATR not applied
    tracker = DummyTracker(positions=[pos], pnl_map={"C": (-100.0, -0.9)})
    event_bus = DummyEventBus()
    state_store = DummyStateStore()
    # Set current price below ATR-based stop so ATR stop will trigger and fallback should be skipped
    data = DummyDataHandler(price_map={"C": 95.0})

    em = make_em(tracker, event_bus, state_store, data)

    # Evaluate rules: ATR exists and is finite -> atr_computed True, ATR stop is entry - 1.5*atr = 100 - 3 = 97
    # current_price 95.0 <= 97 -> ATR stop will trigger. We expect an ExitSignalEvent.
    sig = em._evaluate_exit_rules(pos, current_price=95.0)
    assert sig is not None
    assert sig.reason in ("stop_loss", "profit_target") or sig.reason == "stop_loss"


@pytest.mark.asyncio
async def test_publish_failure_does_not_mark_pending_exit():
    # Prepare a position that will trigger an exit (fixed pct fallback)
    pos = PositionData(
        symbol="X",
        side="long",
        qty=1.0,
        entry_price=100.0,
        entry_time=datetime.now(timezone.utc),
        extreme_price=100.0,
        atr=None,
        trailing_stop_price=None,
        trailing_stop_activated=False,
    )

    # Tracker returns large negative pnl so fallback stop triggers
    tracker = DummyTracker(positions=[pos], pnl_map={"X": (-10.0, -0.5)})
    # Data handler returns low price
    data = DummyDataHandler(price_map={"X": 1.0})
    # EventBus that raises on publish
    event_bus = DummyEventBus(raise_on_publish=True)
    state_store = DummyStateStore()

    em = make_em(tracker, event_bus, state_store, data)

    # Run the async check_positions which will attempt to publish and encounter an exception
    await em.check_positions()

    # Publish failed so signals list may be empty; ensure position.pending_exit remains False
    assert pos.pending_exit is False


@pytest.mark.asyncio
async def test_circuit_breaker_trips_close_all(monkeypatch):
    # When circuit breaker state is 'tripped', monitor loop should call close_all_positions
    tracker = DummyTracker(positions=[])
    event_bus = DummyEventBus()
    state_store = DummyStateStore(cb_state="tripped")
    data = DummyDataHandler()

    em = make_em(tracker, event_bus, state_store, data, check_interval_seconds=0)

    called = {"closed": False}

    async def fake_close_all_positions(reason=""):
        called["closed"] = True
        return []

    em.close_all_positions = fake_close_all_positions

    # Run one iteration of the monitor loop (it will call close_all_positions and then sleep)
    async def run_once():
        em._running = True
        # run loop but abort quickly
        task = asyncio.create_task(em._monitor_loop())
        await asyncio.sleep(0)
        em._running = False
        task.cancel()

    await run_once()

    assert called["closed"] is True
