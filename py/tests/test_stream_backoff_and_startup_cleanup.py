import asyncio
import time

import pytest

from src.stream import Stream, StreamError


class DummyStream:
    def __init__(self):
        self.closed = False

    async def close(self):
        # simulate async close
        await asyncio.sleep(0)
        self.closed = True


@pytest.mark.asyncio
async def test_reconnect_market_stream_clamps_negative_backoff(monkeypatch):
    s = Stream(api_key="x", secret_key="y", paper=True)

    # Simulate rate limiter state: not limited, not ready to retry
    s.market_rate_limiter.is_limited = False
    monkeypatch.setattr(s.market_rate_limiter, "is_ready_to_retry", lambda: False)

    # Make backoff small and last_failure_time sufficiently old so remaining < 0
    monkeypatch.setattr(s.market_rate_limiter, "get_backoff_delay", lambda: 1.0)
    s.market_rate_limiter.last_failure_time = time.time() - 5.0

    # Monkeypatch start methods to be no-ops
    monkeypatch.setattr(s, "_start_stock_stream", lambda symbols: asyncio.sleep(0))
    monkeypatch.setattr(s, "_start_crypto_stream", lambda symbols: asyncio.sleep(0))
    monkeypatch.setattr(s, "_start_trade_stream", lambda: asyncio.sleep(0))

    # Should not raise and should return True (no actual symbols to start)
    result = await s.reconnect_market_stream([])
    assert result is True


@pytest.mark.asyncio
async def test_start_cleans_up_partially_started_streams(monkeypatch):
    s = Stream(api_key="x", secret_key="y", paper=True)

    # Provide one equity symbol so _start_stock_stream will be invoked
    symbols = ["AAPL"]

    async def fake_start_stock(symbols_list):
        # Emulate creating a running stock_stream
        s.stock_stream = DummyStream()
        s.stock_stream_connected = True

    async def fake_start_trade_raises():
        raise RuntimeError("trade stream failed")

    monkeypatch.setattr(s, "_start_stock_stream", fake_start_stock)
    monkeypatch.setattr(s, "_start_crypto_stream", lambda symbols: asyncio.sleep(0))
    monkeypatch.setattr(s, "_start_trade_stream", fake_start_trade_raises)

    with pytest.raises(StreamError):
        await s.start(symbols)

    # After failure, any started streams should be cleaned up
    assert s.stock_stream is None or getattr(s.stock_stream, "closed", False) is True
    assert s.stock_stream_connected is False
