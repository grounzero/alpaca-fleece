import time

import pytest

from src.stream import Stream
from src.stream_polling import StreamPolling
from src.utils import batch_iter


@pytest.mark.asyncio
async def test_reconnect_rate_limit_path():
    s = Stream(api_key="k", secret_key="s")

    called = {"disconnect": False}

    async def on_disconn():
        called["disconnect"] = True

    s.on_market_disconnect = on_disconn
    # Simulate HTTP 429 state
    s.market_rate_limiter.is_limited = True

    res = await s.reconnect_market_stream(["A", "B"])

    assert res is False
    assert called["disconnect"] is True


@pytest.mark.asyncio
async def test_reconnect_wait_backoff(monkeypatch):
    s = Stream(api_key="k", secret_key="s")

    # Simulate previous failures so backoff is required
    # Freeze time for deterministic remaining-time calculation
    monkeypatch.setattr(time, "time", lambda: 2000.0)
    s.market_rate_limiter.failures = 2
    s.market_rate_limiter.last_failure_time = time.time()

    sleep_calls = []

    async def fake_sleep(t):
        sleep_calls.append(t)

    monkeypatch.setattr("asyncio.sleep", fake_sleep)

    # Make start methods raise a 429-like ValueError to exercise failure handling
    async def fake_start(symbols, batch_size=10, batch_delay=1.0):
        raise ValueError("429 Too Many Requests")

    # Mock both stock and crypto stream starts (reconnect calls both)
    monkeypatch.setattr(s, "_start_stock_stream", fake_start)
    monkeypatch.setattr(s, "_start_crypto_stream", fake_start)

    res = await s.reconnect_market_stream(["A"])

    # Backoff branch should have awaited sleep: ensure we slept at least once
    assert len(sleep_calls) > 0
    # And at least one recorded delay should be a positive number (a real backoff)
    assert any(t is not None and t > 0 for t in sleep_calls)
    assert res is False


def test_polling_batch_edge_cases():
    # Validate batching behavior for exact multiples and edge counts
    sp = StreamPolling(api_key="k", secret_key="s", batch_size=5)

    def batches_for(n):
        syms = [f"S{i}" for i in range(n)]
        batches = list(batch_iter(syms, sp.batch_size))
        return batches

    # Exactly one batch
    b1 = batches_for(5)
    assert len(b1) == 1
    assert len(b1[0]) == 5

    # Exactly two batches
    b2 = batches_for(10)
    assert len(b2) == 2
    assert all(len(b) == 5 for b in b2)

    # Non-exact multiple
    b3 = batches_for(11)
    assert len(b3) == 3
    assert [len(x) for x in b3] == [5, 5, 1]
