import asyncio

import pytest

from src.stream import Stream


class DummyStream:
    def __init__(self):
        self.closed = False

    async def close(self):
        await asyncio.sleep(0)
        self.closed = True


@pytest.mark.asyncio
async def test_reconnect_partial_failure_crypto_fails_cleans_stock():
    s = Stream("key", "secret", paper=True, feed="iex", crypto_symbols=["BTC/USD"])

    symbols = ["AAPL", "BTC/USD"]

    async def fake_start_stock(equities):
        # Simulate a successful stock stream start
        s.stock_stream = DummyStream()
        s.stock_stream_connected = True

    async def fake_start_crypto(cryptos):
        # Simulate failure when starting crypto stream
        raise RuntimeError("crypto start failed")

    s._start_stock_stream = fake_start_stock
    s._start_crypto_stream = fake_start_crypto

    # Ensure rate limiter allows attempt
    s.market_rate_limiter.failures = 0
    s.market_rate_limiter.is_limited = False

    result = await s.reconnect_market_stream(symbols)

    assert result is False
    # Stock stream should have been closed and cleared on cleanup
    assert s.stock_stream is None
    assert s.stock_stream_connected is False


@pytest.mark.asyncio
async def test_reconnect_partial_failure_stock_fails_cleans_crypto():
    s = Stream("key", "secret", paper=True, feed="iex", crypto_symbols=["BTC/USD"])

    symbols = ["AAPL", "BTC/USD"]

    async def fake_start_stock(equities):
        # Simulate failure when starting stock stream
        raise RuntimeError("stock start failed")

    async def fake_start_crypto(cryptos):
        # Simulate successful crypto stream start
        s.crypto_stream = DummyStream()
        s.crypto_stream_connected = True

    s._start_stock_stream = fake_start_stock
    s._start_crypto_stream = fake_start_crypto

    # Ensure rate limiter allows attempt
    s.market_rate_limiter.failures = 0
    s.market_rate_limiter.is_limited = False

    result = await s.reconnect_market_stream(symbols)

    assert result is False
    # Crypto stream should have been closed and cleared on cleanup
    assert s.crypto_stream is None
    assert s.crypto_stream_connected is False
