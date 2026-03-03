import time

import pytest

from src.stream import Stream
from src.stream_polling import StreamPolling


@pytest.mark.asyncio
async def test_effective_batch_size_and_start_logging_sip_fallback(caplog):
    sp = StreamPolling(
        "key", "secret", paper=True, feed="sip", batch_size=5, crypto_symbols=["BTC/USD"]
    )

    # Stub feed validation to simulate SIP -> fallback to IEX
    async def fake_validate():
        sp._use_fallback = True

    sp._validate_feed = fake_validate

    # Replace polling loops so start() doesn't spawn long-running tasks
    async def nop_loop():
        return

    sp._poll_loop = nop_loop
    sp._poll_order_updates = nop_loop

    caplog.set_level("INFO")
    await sp.start(["AAPL", "GOOG", "BTC/USD"])

    # When fallback is active the startup message should mention the reduced batch
    messages = "\n".join(r.message.lower() for r in caplog.records)
    assert "reduced for iex" in messages or "reduced for iex feed" in messages

    # Stop should cancel any created tasks (no-op loops)
    await sp.stop()


def test_hex_to_uuid_roundtrips_and_handles_invalid():
    sp = StreamPolling("k", "s")

    hex32 = "51057fad52fa6ca251057fad52fa6ca2"
    uuid_str = sp._hex_to_uuid(hex32)
    assert isinstance(uuid_str, str)
    assert "-" in uuid_str and len(uuid_str) == 36

    # Hyphenated UUID should be returned unchanged (or parseable to same string)
    assert sp._hex_to_uuid(uuid_str) == uuid_str

    # None should be preserved
    assert sp._hex_to_uuid(None) is None

    # Short/invalid strings are returned as-is
    assert sp._hex_to_uuid("short") == "short"


@pytest.mark.asyncio
async def test_reconnect_clamps_negative_backoff_and_proceeds(caplog):
    s = Stream("k", "s", feed="iex")

    # Simulate a recent failure whose backoff has already elapsed
    s.market_rate_limiter.failures = 1
    s.market_rate_limiter.last_failure_time = time.time() - 10.0

    # Stub start helpers so reconnect proceeds without real network
    async def fake_start_stock(symbols):
        s.stock_stream_connected = True

    s._start_stock_stream = fake_start_stock

    caplog.set_level("DEBUG")
    ok = await s.reconnect_market_stream(["AAPL"])
    assert ok is True
    # Accept either the backoff-elapsed debug message or the successful reconnect info
    msgs = [r.message.lower() for r in caplog.records]
    assert any("backoff already elapsed" in m or "market streams reconnected" in m for m in msgs)


@pytest.mark.asyncio
async def test_reconnect_gives_up_when_limited():
    s = Stream("k", "s")
    s.market_rate_limiter.is_limited = True

    called = {"flag": False}

    async def on_disconnect():
        called["flag"] = True

    s.on_market_disconnect = on_disconnect

    ok = await s.reconnect_market_stream(["AAPL"])
    assert ok is False
    assert called["flag"] is True
