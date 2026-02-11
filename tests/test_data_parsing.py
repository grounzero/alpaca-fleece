from datetime import datetime, timedelta, timezone

import pytest

from src.data.bars import BarsHandler
from src.data.order_updates import OrderUpdatesHandler
from src.data.snapshots import SnapshotsHandler
from src.event_bus import BarEvent, OrderUpdateEvent


class DummyRawBar:
    def __init__(
        self, symbol, timestamp, open, high, low, close, volume, trade_count=None, vwap=None
    ):
        self.symbol = symbol
        self.timestamp = timestamp
        self.open = open
        self.high = high
        self.low = low
        self.close = close
        self.volume = volume
        if trade_count is not None:
            self.trade_count = trade_count
        if vwap is not None:
            self.vwap = vwap


def test_bar_parsing_with_missing_optional_fields():
    # Create raw bar missing optional trade_count and vwap
    ts = datetime.now(timezone.utc)
    raw = DummyRawBar("TST", ts, 1.0, 2.0, 0.5, 1.5, 100)

    # Use BarsHandler._to_canonical_bar directly (no DB required)
    bh = BarsHandler(state_store=None, event_bus=None, market_data_client=None)
    evt = bh._to_canonical_bar(raw)

    assert isinstance(evt, BarEvent)
    assert evt.symbol == "TST"
    assert evt.trade_count is None
    assert evt.vwap is None


def test_bar_parsing_missing_required_raises():
    ts = datetime.now(timezone.utc)

    # Missing 'open' attribute
    class BrokenBar:
        def __init__(self):
            self.symbol = "X"
            self.timestamp = ts

    bh = BarsHandler(state_store=None, event_bus=None, market_data_client=None)
    with pytest.raises(AttributeError):
        bh._to_canonical_bar(BrokenBar())


def test_get_dataframe_edge_timestamps():
    bh = BarsHandler(state_store=None, event_bus=None, market_data_client=None)

    t0 = datetime(2026, 2, 8, 10, 0, tzinfo=timezone.utc)
    t1 = t0 + timedelta(minutes=1)

    b0 = BarEvent(
        symbol="A",
        timestamp=t0,
        open=1,
        high=2,
        low=0.5,
        close=1.5,
        volume=10,
        trade_count=1,
        vwap=1.2,
    )
    b1 = BarEvent(
        symbol="A",
        timestamp=t1,
        open=1.5,
        high=2.5,
        low=1.0,
        close=2.0,
        volume=20,
        trade_count=2,
        vwap=1.75,
    )

    bh.bars_deque["A"] = []
    bh.bars_deque["A"].append(b0)
    bh.bars_deque["A"].append(b1)

    df = bh.get_dataframe("A")
    assert df is not None
    # Index should match timestamps in order
    assert list(df.index) == [t0, t1]
    assert df.loc[t0, "close"] == 1.5
    assert df.loc[t1, "close"] == 2.0


def test_order_normalisation_missing_fill_price():
    # Build minimal raw_update object
    class Status:
        def __init__(self, value):
            self.value = value

    class RawOrder:
        def __init__(self):
            self.id = "oid"
            self.client_order_id = "cid"
            self.symbol = "SYM"
            self.side = Status("buy")
            self.status = Status("filled")
            self.filled_qty = None
            self.filled_avg_price = None

    class RawUpdate:
        def __init__(self):
            self.order = RawOrder()
            self.at = None

    ouh = OrderUpdatesHandler(state_store=None, event_bus=None)
    evt = ouh._to_canonical_order_update(RawUpdate())

    assert isinstance(evt, OrderUpdateEvent)
    assert evt.avg_fill_price is None
    assert evt.filled_qty == 0


def test_snapshots_cache_and_error(monkeypatch):
    # Dummy client that returns a snapshot dict
    class Client:
        def __init__(self):
            self.called = 0

        def get_snapshot(self, symbol):
            self.called += 1
            if symbol == "ERR":
                raise ConnectionError("boom")
            return {"bid": 1.0, "ask": 1.2, "bid_size": 10, "ask_size": 12}

    client = Client()
    sh = SnapshotsHandler(market_data_client=client, cache_ttl_sec=60)

    data = sh.get_snapshot("X")
    assert data["bid"] == 1.0
    # cached: second call should not increment client.called
    before = client.called
    _ = sh.get_snapshot("X")
    assert client.called == before

    # error path: no cache and client raises -> returns None
    res = sh.get_snapshot("ERR")
    assert res is None
