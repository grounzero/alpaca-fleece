import asyncio

import pytest

import src.stream as stream_mod


def test_register_handlers_sets_callbacks():
    s = stream_mod.Stream(api_key="k", secret_key="s", paper=True, feed="iex")

    def on_bar(x):
        return x

    def on_order_update(x):
        return x

    def on_market_disconnect():
        return None

    def on_trade_disconnect():
        return None

    s.register_handlers(on_bar, on_order_update, on_market_disconnect, on_trade_disconnect)

    assert s.on_bar is on_bar
    assert s.on_order_update is on_order_update
    assert s.on_market_disconnect is on_market_disconnect
    assert s.on_trade_disconnect is on_trade_disconnect


@pytest.mark.asyncio
async def test_start_stock_stream_subscribes_in_batches(monkeypatch):
    # Dummy StockDataStream to capture subscribe_bars calls
    class DummyStockDataStream:
        def __init__(self, api_key, secret_key, feed):
            self.api_key = api_key
            self.secret_key = secret_key
            self.feed = feed
            self.subscriptions = []

        def subscribe_bars(self, handler, *symbols):
            # record subscription tuple
            self.subscriptions.append((handler, tuple(symbols)))

        async def _run_forever(self):
            # short noop to allow create_task to complete
            await asyncio.sleep(0)

        async def close(self):
            return None

    monkeypatch.setattr(stream_mod, "StockDataStream", DummyStockDataStream)

    # Speed up test by patching asyncio.sleep to still yield control but not delay
    original_sleep = asyncio.sleep

    async def _noop_sleep(*a, **k):
        await original_sleep(0)

    monkeypatch.setattr(asyncio, "sleep", _noop_sleep)

    s = stream_mod.Stream(api_key="k", secret_key="s", feed="iex")

    # register a simple async handler
    async def on_bar(bar):
        return None

    s.register_handlers(
        on_bar=on_bar,
        on_order_update=lambda u: None,
        on_market_disconnect=lambda: None,
        on_trade_disconnect=lambda: None,
    )

    # Run start stock stream with 3 symbols, batch_size=2
    await s._start_stock_stream(["A", "B", "C"], batch_size=2, batch_delay=0)

    assert s.stock_stream_connected is True
    # Two batches expected: (A,B) and (C,)
    subs = s.stock_stream.subscriptions
    assert len(subs) == 2
    assert subs[0][1] == ("A", "B")
    assert subs[1][1] == ("C",)


@pytest.mark.asyncio
async def test_start_trade_stream_subscribes_and_sets_connected(monkeypatch):
    class DummyTradingStream:
        def __init__(self, api_key, secret_key):
            self.api_key = api_key
            self.secret_key = secret_key
            self.subscribed = False

        def subscribe_trade_updates(self, handler):
            self.subscribed = True

        async def _run_forever(self):
            await asyncio.sleep(0)

        async def close(self):
            return None

    monkeypatch.setattr(stream_mod, "TradingStream", DummyTradingStream)

    # Speed up test by patching asyncio.sleep to still yield control but not delay
    original_sleep = asyncio.sleep

    async def _noop_sleep(*a, **k):
        await original_sleep(0)

    monkeypatch.setattr(asyncio, "sleep", _noop_sleep)

    s = stream_mod.Stream(api_key="k", secret_key="s")
    await s._start_trade_stream()

    assert s.trade_connected is True
    assert isinstance(s.trade_updates_stream, DummyTradingStream)
