"""Tests for crypto symbol WebSocket support in Stream.

Tests cover:
- Crypto symbols are routed to CryptoDataStream
- Equity symbols are routed to StockDataStream
- Mixed symbol lists are properly separated and subscribed
- Both streams run concurrently
- Reconnection handles both stream types
"""

import asyncio

import pytest

import src.stream as stream_mod


@pytest.fixture
def dummy_stock_stream():
    """Create a dummy StockDataStream for testing."""

    class DummyStockDataStream:
        def __init__(self, api_key, secret_key, feed):
            self.api_key = api_key
            self.secret_key = secret_key
            self.feed = feed
            self.subscriptions = []

        def subscribe_bars(self, handler, *symbols):
            self.subscriptions.append((handler, tuple(symbols)))

        async def _run_forever(self):
            await asyncio.sleep(0)

        async def close(self):
            return None

    return DummyStockDataStream


@pytest.fixture
def dummy_crypto_stream():
    """Create a dummy CryptoDataStream for testing."""

    class DummyCryptoDataStream:
        def __init__(self, api_key, secret_key):
            self.api_key = api_key
            self.secret_key = secret_key
            self.subscriptions = []

        def subscribe_bars(self, handler, *symbols):
            self.subscriptions.append((handler, tuple(symbols)))

        async def _run_forever(self):
            await asyncio.sleep(0)

        async def close(self):
            return None

    return DummyCryptoDataStream


@pytest.fixture
def noop_sleep(monkeypatch):
    """Patch asyncio.sleep to not delay but still yield control."""
    original_sleep = asyncio.sleep

    async def _noop_sleep(*a, **k):
        await original_sleep(0)

    monkeypatch.setattr(asyncio, "sleep", _noop_sleep)


class TestCryptoWebSocket:
    """Test crypto symbol WebSocket support."""

    @pytest.mark.asyncio
    async def test_start_separates_equity_and_crypto(
        self, monkeypatch, dummy_stock_stream, dummy_crypto_stream, noop_sleep
    ):
        """Test that start() separates symbols into equity and crypto."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        # Mock TradingStream to prevent trade stream startup
        class DummyTradingStream:
            def __init__(self, api_key, secret_key):
                pass

            def subscribe_trade_updates(self, handler):
                pass

            async def _run_forever(self):
                await asyncio.sleep(0)

        monkeypatch.setattr(stream_mod, "TradingStream", DummyTradingStream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=["BTC/USD", "ETH/USD"])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s.start(["AAPL", "MSFT", "BTC/USD", "ETH/USD", "GOOGL"])

        # Verify symbol separation
        assert set(s._equity_symbols) == {"AAPL", "MSFT", "GOOGL"}
        assert set(s._crypto_symbols) == {"BTC/USD", "ETH/USD"}

    @pytest.mark.asyncio
    async def test_equity_symbols_use_stock_stream(
        self, monkeypatch, dummy_stock_stream, noop_sleep
    ):
        """Test that equity symbols are subscribed to StockDataStream."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)

        s = stream_mod.Stream(api_key="k", secret_key="s")

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s._start_stock_stream(["AAPL", "MSFT"], batch_size=2, batch_delay=0)

        # Verify stock stream was created and subscribed
        assert s.stock_stream is not None
        assert s.stock_stream_connected is True
        assert len(s.stock_stream.subscriptions) == 1
        assert s.stock_stream.subscriptions[0][1] == ("AAPL", "MSFT")

    @pytest.mark.asyncio
    async def test_crypto_symbols_use_crypto_stream(
        self, monkeypatch, dummy_crypto_stream, noop_sleep
    ):
        """Test that crypto symbols are subscribed to CryptoDataStream."""
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=["BTC/USD"])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s._start_crypto_stream(["BTC/USD", "ETH/USD"], batch_size=2, batch_delay=0)

        # Verify crypto stream was created and subscribed
        assert s.crypto_stream is not None
        assert s.crypto_stream_connected is True
        assert len(s.crypto_stream.subscriptions) == 1
        assert s.crypto_stream.subscriptions[0][1] == ("BTC/USD", "ETH/USD")

    @pytest.mark.asyncio
    async def test_crypto_stream_batching(self, monkeypatch, dummy_crypto_stream, noop_sleep):
        """Test that crypto symbols are batched correctly."""
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        s = stream_mod.Stream(
            api_key="k",
            secret_key="s",
            crypto_symbols=["BTC/USD", "ETH/USD", "SOL/USD"],
        )

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        # Subscribe with batch_size=2
        await s._start_crypto_stream(["BTC/USD", "ETH/USD", "SOL/USD"], batch_size=2, batch_delay=0)

        # Should have 2 batches: (BTC/USD, ETH/USD) and (SOL/USD,)
        assert len(s.crypto_stream.subscriptions) == 2
        assert s.crypto_stream.subscriptions[0][1] == ("BTC/USD", "ETH/USD")
        assert s.crypto_stream.subscriptions[1][1] == ("SOL/USD",)

    @pytest.mark.asyncio
    async def test_both_streams_connect_concurrently(
        self, monkeypatch, dummy_stock_stream, dummy_crypto_stream, noop_sleep
    ):
        """Test that both stock and crypto streams connect when both symbol types present."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        # Mock TradingStream
        class DummyTradingStream:
            def __init__(self, api_key, secret_key):
                pass

            def subscribe_trade_updates(self, handler):
                pass

            async def _run_forever(self):
                await asyncio.sleep(0)

        monkeypatch.setattr(stream_mod, "TradingStream", DummyTradingStream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=["BTC/USD", "ETH/USD"])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s.start(["AAPL", "MSFT", "BTC/USD", "ETH/USD"])

        # Both streams should be connected
        assert s.stock_stream_connected is True
        assert s.crypto_stream_connected is True
        assert s.market_connected is True

        # Verify subscriptions
        assert len(s.stock_stream.subscriptions) > 0
        assert len(s.crypto_stream.subscriptions) > 0

    @pytest.mark.asyncio
    async def test_only_crypto_symbols(self, monkeypatch, dummy_crypto_stream, noop_sleep):
        """Test that stream works with only crypto symbols (no equity)."""
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        # Mock TradingStream
        class DummyTradingStream:
            def __init__(self, api_key, secret_key):
                pass

            def subscribe_trade_updates(self, handler):
                pass

            async def _run_forever(self):
                await asyncio.sleep(0)

        monkeypatch.setattr(stream_mod, "TradingStream", DummyTradingStream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=["BTC/USD", "ETH/USD"])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s.start(["BTC/USD", "ETH/USD"])

        # Only crypto stream should be connected
        assert s.stock_stream is None
        assert s.stock_stream_connected is False
        assert s.crypto_stream_connected is True
        assert s.market_connected is True

    @pytest.mark.asyncio
    async def test_only_equity_symbols(self, monkeypatch, dummy_stock_stream, noop_sleep):
        """Test that stream works with only equity symbols (no crypto)."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)

        # Mock TradingStream
        class DummyTradingStream:
            def __init__(self, api_key, secret_key):
                pass

            def subscribe_trade_updates(self, handler):
                pass

            async def _run_forever(self):
                await asyncio.sleep(0)

        monkeypatch.setattr(stream_mod, "TradingStream", DummyTradingStream)

        s = stream_mod.Stream(api_key="k", secret_key="s")  # No crypto_symbols

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s.start(["AAPL", "MSFT"])

        # Only stock stream should be connected
        assert s.stock_stream_connected is True
        assert s.crypto_stream is None
        assert s.crypto_stream_connected is False
        assert s.market_connected is True

    @pytest.mark.asyncio
    async def test_stop_closes_both_streams(
        self, monkeypatch, dummy_stock_stream, dummy_crypto_stream, noop_sleep
    ):
        """Test that stop() closes both stock and crypto streams."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=["BTC/USD"])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s._start_stock_stream(["AAPL"], batch_size=10, batch_delay=0)
        await s._start_crypto_stream(["BTC/USD"], batch_size=10, batch_delay=0)

        # Both should be connected
        assert s.stock_stream_connected is True
        assert s.crypto_stream_connected is True

        await s.stop()

        # Both should be disconnected
        assert s.stock_stream_connected is False
        assert s.crypto_stream_connected is False
        assert s.market_connected is False

    @pytest.mark.asyncio
    async def test_reconnect_handles_both_streams(
        self, monkeypatch, dummy_stock_stream, dummy_crypto_stream, noop_sleep
    ):
        """Test that reconnect_market_stream handles both stock and crypto streams."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=["BTC/USD", "ETH/USD"])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        # Simulate reconnection
        result = await s.reconnect_market_stream(["AAPL", "MSFT", "BTC/USD", "ETH/USD"])

        assert result is True
        assert s.stock_stream_connected is True
        assert s.crypto_stream_connected is True
        assert s.market_connected is True

        # Verify both streams have subscriptions
        assert len(s.stock_stream.subscriptions) > 0
        assert len(s.crypto_stream.subscriptions) > 0

    @pytest.mark.asyncio
    async def test_empty_crypto_symbols_list(self, monkeypatch, dummy_stock_stream, noop_sleep):
        """Test that empty crypto_symbols list works correctly."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)

        # Mock TradingStream
        class DummyTradingStream:
            def __init__(self, api_key, secret_key):
                pass

            def subscribe_trade_updates(self, handler):
                pass

            async def _run_forever(self):
                await asyncio.sleep(0)

        monkeypatch.setattr(stream_mod, "TradingStream", DummyTradingStream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=[])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        await s.start(["AAPL", "MSFT"])

        # No crypto stream should exist
        assert s.crypto_stream is None
        assert s.crypto_stream_connected is False
        assert s.stock_stream_connected is True

    @pytest.mark.asyncio
    async def test_reconnect_closes_existing_streams_first(
        self, monkeypatch, dummy_stock_stream, dummy_crypto_stream, noop_sleep
    ):
        """Test that reconnect closes existing streams before creating new ones."""
        monkeypatch.setattr(stream_mod, "StockDataStream", dummy_stock_stream)
        monkeypatch.setattr(stream_mod, "CryptoDataStream", dummy_crypto_stream)

        s = stream_mod.Stream(api_key="k", secret_key="s", crypto_symbols=["BTC/USD", "ETH/USD"])

        async def on_bar(bar):
            return None

        s.register_handlers(
            on_bar=on_bar,
            on_order_update=lambda u: None,
            on_market_disconnect=lambda: None,
            on_trade_disconnect=lambda: None,
        )

        # Start initial streams
        await s._start_stock_stream(["AAPL"], batch_size=10, batch_delay=0)
        await s._start_crypto_stream(["BTC/USD"], batch_size=10, batch_delay=0)

        old_stock_stream = s.stock_stream
        old_crypto_stream = s.crypto_stream

        # Track close() calls
        stock_close_called = False
        crypto_close_called = False

        async def track_stock_close():
            nonlocal stock_close_called
            stock_close_called = True

        async def track_crypto_close():
            nonlocal crypto_close_called
            crypto_close_called = True

        old_stock_stream.close = track_stock_close
        old_crypto_stream.close = track_crypto_close

        # Reconnect
        result = await s.reconnect_market_stream(["AAPL", "MSFT", "BTC/USD", "ETH/USD"])

        # Verify old streams were closed
        assert stock_close_called is True
        assert crypto_close_called is True

        # Verify new streams were created (different instances)
        assert s.stock_stream is not old_stock_stream
        assert s.crypto_stream is not old_crypto_stream

        # Verify connection flags
        assert s.stock_stream_connected is True
        assert s.crypto_stream_connected is True
        assert result is True
