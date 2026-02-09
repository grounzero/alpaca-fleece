import time

import pytest

import src.stream as stream_mod


@pytest.mark.asyncio
async def test_reconnect_rate_limited_calls_disconnect(monkeypatch):
    s = stream_mod.Stream(api_key="k", secret_key="s")
    called = {"disc": False}

    async def on_market_disconnect():
        called["disc"] = True

    s.register_handlers(
        on_bar=lambda b: None,
        on_order_update=lambda u: None,
        on_market_disconnect=on_market_disconnect,
        on_trade_disconnect=lambda: None,
    )

    # Simulate rate limiter hitting hard limit
    s.market_rate_limiter.is_limited = True

    res = await s.reconnect_market_stream(["A"])
    assert res is False
    assert called["disc"] is True


@pytest.mark.asyncio
async def test_reconnect_handles_429_and_records_failure(monkeypatch):
    s = stream_mod.Stream(api_key="k", secret_key="s")

    # Make sure we appear to be in backoff (failures > 0 and not ready)
    s.market_rate_limiter.failures = 1
    s.market_rate_limiter.last_failure_time = time.time()

    # No-op sleep to avoid waiting
    async def noop_sleep(x):
        return None

    monkeypatch.setattr(stream_mod.asyncio, "sleep", noop_sleep)

    async def fake_start(self, symbols, *args, **kwargs):
        raise ValueError("429 Too Many Requests")

    monkeypatch.setattr(stream_mod.Stream, "_start_market_stream", fake_start)

    res = await s.reconnect_market_stream(["A"])
    assert res is False
    # Failures should have incremented
    assert s.market_rate_limiter.failures >= 1


@pytest.mark.asyncio
async def test_reconnect_success_resets_rate_limiter(monkeypatch):
    s = stream_mod.Stream(api_key="k", secret_key="s")
    # Freeze time so last_failure_time calculation is deterministic
    monkeypatch.setattr(time, "time", lambda: 2000.0)
    s.market_rate_limiter.failures = 1
    s.market_rate_limiter.last_failure_time = time.time() - 1000.0  # ready to retry

    async def fake_start(self, symbols, *args, **kwargs):
        # simulate successful start
        return None

    monkeypatch.setattr(stream_mod.Stream, "_start_market_stream", fake_start)

    res = await s.reconnect_market_stream(["A"])
    assert res is True
    assert s.market_rate_limiter.failures == 0


@pytest.mark.asyncio
async def test_reconnect_generic_exception_records_failure(monkeypatch):
    s = stream_mod.Stream(api_key="k", secret_key="s")
    s.market_rate_limiter.failures = 0

    async def fake_start(self, symbols, *args, **kwargs):
        raise RuntimeError("boom")

    monkeypatch.setattr(stream_mod.Stream, "_start_market_stream", fake_start)

    res = await s.reconnect_market_stream(["A"])
    assert res is False
    assert s.market_rate_limiter.failures >= 1
