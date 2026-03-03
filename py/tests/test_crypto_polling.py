"""Tests for crypto symbol polling support in StreamPolling.

Tests cover:
- Crypto symbols are routed to CryptoHistoricalDataClient
- Equity symbols are routed to StockHistoricalDataClient
- Mixed symbol lists are properly separated and polled
- CryptoBarsRequest parameters are correct
- Error handling for crypto-specific failures
- Batch processing for multiple crypto symbols
"""

from datetime import datetime, timezone
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from alpaca.data.requests import CryptoBarsRequest
from alpaca.data.timeframe import TimeFrame

from src.stream_polling import StreamPolling


class TestCryptoPolling:
    """Test crypto symbol polling support."""

    @pytest.fixture
    def mock_stream_with_crypto(self):
        """Create a StreamPolling instance with crypto symbols configured."""
        with (
            patch("src.stream_polling.StockHistoricalDataClient"),
            patch("src.stream_polling.CryptoHistoricalDataClient"),
        ):
            stream = StreamPolling(
                "api_key",
                "secret_key",
                crypto_symbols=["BTC/USD", "ETH/USD", "SOL/USD"],
            )
            stream.stock_client = MagicMock()
            stream.crypto_client = MagicMock()
            return stream

    @pytest.fixture
    def sample_bar(self):
        """Create a sample bar object."""
        bar = MagicMock()
        bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        bar.open = 100.0
        bar.high = 101.0
        bar.low = 99.0
        bar.close = 100.5
        bar.volume = 1000
        bar.vwap = 100.2
        return bar

    @pytest.mark.asyncio
    async def test_crypto_symbols_separated_on_start(self, mock_stream_with_crypto):
        """Test that symbols are separated into equity and crypto on start."""
        symbols = ["AAPL", "MSFT", "BTC/USD", "ETH/USD", "GOOGL"]

        # Mock the polling tasks to prevent infinite loops
        mock_stream_with_crypto._poll_loop = AsyncMock()
        mock_stream_with_crypto._poll_order_updates = AsyncMock()

        await mock_stream_with_crypto.start(symbols)

        # Verify symbols are separated correctly
        assert set(mock_stream_with_crypto._equity_symbols) == {"AAPL", "MSFT", "GOOGL"}
        assert set(mock_stream_with_crypto._crypto_symbols) == {"BTC/USD", "ETH/USD"}

        await mock_stream_with_crypto.stop()

    @pytest.mark.asyncio
    async def test_equity_batch_uses_stock_client(self, mock_stream_with_crypto, sample_bar):
        """Test that equity symbols use stock client."""
        mock_response = MagicMock()
        mock_response.data = {"AAPL": [sample_bar]}
        mock_stream_with_crypto.stock_client.get_stock_bars.return_value = mock_response

        await mock_stream_with_crypto._poll_equity_batch(["AAPL"])

        # Verify stock client was called
        mock_stream_with_crypto.stock_client.get_stock_bars.assert_called_once()
        # Verify crypto client was not called
        mock_stream_with_crypto.crypto_client.get_crypto_bars.assert_not_called()

    @pytest.mark.asyncio
    async def test_crypto_batch_uses_crypto_client(self, mock_stream_with_crypto, sample_bar):
        """Test that crypto symbols use crypto client."""
        mock_response = MagicMock()
        mock_response.data = {"BTC/USD": [sample_bar]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Verify crypto client was called
        mock_stream_with_crypto.crypto_client.get_crypto_bars.assert_called_once()
        # Verify stock client was not called
        mock_stream_with_crypto.stock_client.get_stock_bars.assert_not_called()

    @pytest.mark.asyncio
    async def test_poll_symbol_routes_equity(self, mock_stream_with_crypto, sample_bar):
        """Test that _poll_symbol routes equity symbols correctly."""
        mock_response = MagicMock()
        mock_response.data = {"AAPL": [sample_bar]}
        mock_stream_with_crypto.stock_client.get_stock_bars.return_value = mock_response

        await mock_stream_with_crypto._poll_symbol("AAPL")

        # Should use stock client
        mock_stream_with_crypto.stock_client.get_stock_bars.assert_called_once()
        mock_stream_with_crypto.crypto_client.get_crypto_bars.assert_not_called()

    @pytest.mark.asyncio
    async def test_poll_symbol_routes_crypto(self, mock_stream_with_crypto, sample_bar):
        """Test that _poll_symbol routes crypto symbols correctly."""
        mock_response = MagicMock()
        mock_response.data = {"BTC/USD": [sample_bar]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        await mock_stream_with_crypto._poll_symbol("BTC/USD")

        # Should use crypto client
        mock_stream_with_crypto.crypto_client.get_crypto_bars.assert_called_once()
        mock_stream_with_crypto.stock_client.get_stock_bars.assert_not_called()

    @pytest.mark.asyncio
    async def test_no_crypto_symbols_config(self):
        """Test that StreamPolling works without crypto_symbols config."""
        with (
            patch("src.stream_polling.StockHistoricalDataClient"),
            patch("src.stream_polling.CryptoHistoricalDataClient"),
        ):
            stream = StreamPolling("api_key", "secret_key")  # No crypto_symbols param
            stream.stock_client = MagicMock()
            stream.crypto_client = MagicMock()

            # Mock the polling tasks
            stream._poll_loop = AsyncMock()
            stream._poll_order_updates = AsyncMock()

            await stream.start(["AAPL", "MSFT"])

            # All symbols should be treated as equity
            assert set(stream._equity_symbols) == {"AAPL", "MSFT"}
            assert stream._crypto_symbols == []

            await stream.stop()

    @pytest.mark.asyncio
    async def test_crypto_symbols_logged_on_start(self, mock_stream_with_crypto, caplog):
        """Test that crypto symbols are logged when present."""
        symbols = ["AAPL", "BTC/USD", "ETH/USD"]

        # Mock the polling tasks
        mock_stream_with_crypto._poll_loop = AsyncMock()
        mock_stream_with_crypto._poll_order_updates = AsyncMock()

        with caplog.at_level("INFO"):
            await mock_stream_with_crypto.start(symbols)

        # Should log symbol routing info
        assert "Symbol routing:" in caplog.text
        assert "1 equity" in caplog.text
        assert "2 crypto" in caplog.text
        assert "BTC/USD" in caplog.text

        await mock_stream_with_crypto.stop()


class TestCryptoAPIDetails:
    """Test crypto API-specific functionality and parameters."""

    @pytest.fixture
    def mock_stream_with_crypto(self):
        """Create a StreamPolling instance with crypto symbols configured."""
        with (
            patch("src.stream_polling.StockHistoricalDataClient"),
            patch("src.stream_polling.CryptoHistoricalDataClient"),
        ):
            stream = StreamPolling(
                "api_key",
                "secret_key",
                crypto_symbols=["BTC/USD", "ETH/USD", "SOL/USD"],
            )
            stream.stock_client = MagicMock()
            stream.crypto_client = MagicMock()
            return stream

    @pytest.fixture
    def sample_bar(self):
        """Create a sample bar object."""
        bar = MagicMock()
        bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        bar.open = 100.0
        bar.high = 101.0
        bar.low = 99.0
        bar.close = 100.5
        bar.volume = 1000
        bar.vwap = 100.2
        bar.trade_count = 500
        return bar

    @pytest.mark.asyncio
    async def test_crypto_bars_request_parameters(self, mock_stream_with_crypto, sample_bar):
        """Test that CryptoBarsRequest is created with correct parameters."""
        mock_response = MagicMock()
        mock_response.data = {"BTC/USD": [sample_bar]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Verify the request parameters
        call_args = mock_stream_with_crypto.crypto_client.get_crypto_bars.call_args
        request = call_args[0][0]

        assert isinstance(request, CryptoBarsRequest)
        assert request.symbol_or_symbols == ["BTC/USD"]
        # TimeFrame.Minute creates new instances, compare string representation
        assert str(request.timeframe) == str(TimeFrame.Minute)
        assert request.limit == 5
        # Verify time window is approximately last 5 minutes
        assert request.start is not None
        assert request.end is not None
        time_diff = (request.end - request.start).total_seconds()
        assert 250 <= time_diff <= 350  # ~5 minutes with some tolerance

    @pytest.mark.asyncio
    async def test_crypto_request_no_feed_parameter(self, mock_stream_with_crypto, sample_bar):
        """Test that crypto requests don't include feed parameter (crypto doesn't use IEX/SIP)."""
        mock_response = MagicMock()
        mock_response.data = {"BTC/USD": [sample_bar]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Get the CryptoBarsRequest that was created
        call_args = mock_stream_with_crypto.crypto_client.get_crypto_bars.call_args
        request = call_args[0][0]

        # CryptoBarsRequest should not have a feed attribute (unlike StockBarsRequest)
        # This is correct behavior - crypto data doesn't support IEX/SIP feeds
        assert not hasattr(request, "feed") or getattr(request, "feed", None) is None

    @pytest.mark.asyncio
    async def test_multiple_crypto_symbols_batch(self, mock_stream_with_crypto, sample_bar):
        """Test polling multiple crypto symbols in a single batch."""
        btc_bar = MagicMock()
        btc_bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        btc_bar.close = 45000.0
        btc_bar.open = 44800.0
        btc_bar.high = 45100.0
        btc_bar.low = 44700.0
        btc_bar.volume = 1000000

        eth_bar = MagicMock()
        eth_bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        eth_bar.close = 2500.0
        eth_bar.open = 2480.0
        eth_bar.high = 2510.0
        eth_bar.low = 2475.0
        eth_bar.volume = 500000

        sol_bar = MagicMock()
        sol_bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        sol_bar.close = 100.0
        sol_bar.open = 99.5
        sol_bar.high = 100.5
        sol_bar.low = 99.0
        sol_bar.volume = 250000

        mock_response = MagicMock()
        mock_response.data = {
            "BTC/USD": [btc_bar],
            "ETH/USD": [eth_bar],
            "SOL/USD": [sol_bar],
        }
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        processed = []
        mock_stream_with_crypto.on_bar = AsyncMock(
            side_effect=lambda bar: processed.append(bar.symbol)
        )

        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD", "ETH/USD", "SOL/USD"])

        # Verify all crypto symbols were processed
        assert set(processed) == {"BTC/USD", "ETH/USD", "SOL/USD"}

        # Verify only one API call was made (batch request)
        assert mock_stream_with_crypto.crypto_client.get_crypto_bars.call_count == 1

    @pytest.mark.asyncio
    async def test_crypto_error_handling(self, mock_stream_with_crypto, caplog):
        """Test that crypto API errors are caught and logged."""
        mock_stream_with_crypto.crypto_client.get_crypto_bars.side_effect = Exception(
            "Crypto API error"
        )

        # Should not raise - error is logged and swallowed
        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Verify error was logged
        assert "Crypto batch polling error" in caplog.text
        assert "BTC/USD" in caplog.text

    @pytest.mark.asyncio
    async def test_crypto_partial_response(self, mock_stream_with_crypto, sample_bar):
        """Test handling when some crypto symbols have no data."""
        btc_bar = MagicMock()
        btc_bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        btc_bar.close = 45000.0
        btc_bar.open = 44800.0
        btc_bar.high = 45100.0
        btc_bar.low = 44700.0
        btc_bar.volume = 1000000

        mock_response = MagicMock()
        mock_response.data = {
            "BTC/USD": [btc_bar],
            "ETH/USD": [],  # No data
        }
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        processed = []
        mock_stream_with_crypto.on_bar = AsyncMock(
            side_effect=lambda bar: processed.append(bar.symbol)
        )

        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD", "ETH/USD"])

        # Should only process symbols with data
        assert processed == ["BTC/USD"]
        assert "ETH/USD" not in processed

    @pytest.mark.asyncio
    async def test_crypto_missing_from_response(self, mock_stream_with_crypto, sample_bar):
        """Test handling when crypto symbol is missing from response entirely."""
        btc_bar = MagicMock()
        btc_bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        btc_bar.close = 45000.0
        btc_bar.open = 44800.0
        btc_bar.high = 45100.0
        btc_bar.low = 44700.0
        btc_bar.volume = 1000000

        mock_response = MagicMock()
        mock_response.data = {
            "BTC/USD": [btc_bar],
            # SOL/USD missing entirely
        }
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        processed = []
        mock_stream_with_crypto.on_bar = AsyncMock(
            side_effect=lambda bar: processed.append(bar.symbol)
        )

        # Should not raise error
        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD", "SOL/USD"])

        assert "SOL/USD" not in processed
        assert len(processed) == 1

    @pytest.mark.asyncio
    async def test_crypto_bars_processed_same_as_equity(self, mock_stream_with_crypto):
        """Test that crypto bars are processed through same handler as equity bars."""
        btc_bar = MagicMock()
        btc_bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        btc_bar.open = 44000.0
        btc_bar.high = 45000.0
        btc_bar.low = 43500.0
        btc_bar.close = 44800.0
        btc_bar.volume = 1000000
        btc_bar.vwap = 44500.0
        btc_bar.trade_count = 5000

        mock_response = MagicMock()
        mock_response.data = {"BTC/USD": [btc_bar]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        bars_received = []

        async def capture_bar(bar):
            bars_received.append(bar)

        mock_stream_with_crypto.on_bar = capture_bar

        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Verify bar was processed
        assert len(bars_received) == 1
        processed_bar = bars_received[0]

        # Verify all fields are preserved
        assert processed_bar.symbol == "BTC/USD"
        assert processed_bar.timestamp == btc_bar.timestamp
        assert processed_bar.open == 44000.0
        assert processed_bar.high == 45000.0
        assert processed_bar.low == 43500.0
        assert processed_bar.close == 44800.0
        assert processed_bar.volume == 1000000

    @pytest.mark.asyncio
    async def test_empty_crypto_symbols_list(self):
        """Test that empty crypto_symbols list doesn't cause errors."""
        with (
            patch("src.stream_polling.StockHistoricalDataClient"),
            patch("src.stream_polling.CryptoHistoricalDataClient"),
        ):
            stream = StreamPolling("api_key", "secret_key", crypto_symbols=[])
            stream.stock_client = MagicMock()
            stream.crypto_client = MagicMock()

            # Mock the polling tasks
            stream._poll_loop = AsyncMock()
            stream._poll_order_updates = AsyncMock()

            await stream.start(["AAPL", "MSFT"])

            # All symbols should be equity
            assert set(stream._equity_symbols) == {"AAPL", "MSFT"}
            assert stream._crypto_symbols == []

            # Crypto client should never be called
            stream.crypto_client.get_crypto_bars.assert_not_called()

            await stream.stop()

    @pytest.mark.asyncio
    async def test_only_crypto_symbols(self):
        """Test polling with only crypto symbols (no equity)."""
        with (
            patch("src.stream_polling.StockHistoricalDataClient"),
            patch("src.stream_polling.CryptoHistoricalDataClient"),
        ):
            stream = StreamPolling("api_key", "secret_key", crypto_symbols=["BTC/USD", "ETH/USD"])
            stream.stock_client = MagicMock()
            stream.crypto_client = MagicMock()

            # Mock the polling tasks
            stream._poll_loop = AsyncMock()
            stream._poll_order_updates = AsyncMock()

            await stream.start(["BTC/USD", "ETH/USD"])

            # No equity symbols
            assert stream._equity_symbols == []
            # All crypto
            assert set(stream._crypto_symbols) == {"BTC/USD", "ETH/USD"}

            await stream.stop()

    @pytest.mark.asyncio
    async def test_crypto_deduplication_same_timestamp(self, mock_stream_with_crypto):
        """Test that crypto bars with same timestamp are deduplicated."""
        btc_bar = MagicMock()
        btc_bar.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        btc_bar.close = 45000.0
        btc_bar.open = 44800.0
        btc_bar.high = 45100.0
        btc_bar.low = 44700.0
        btc_bar.volume = 1000000

        mock_response = MagicMock()
        mock_response.data = {"BTC/USD": [btc_bar]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response

        processed = []
        mock_stream_with_crypto.on_bar = AsyncMock(
            side_effect=lambda bar: processed.append(bar.symbol)
        )

        # Poll same bar twice
        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])
        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Should only process once (deduplication by timestamp)
        assert len(processed) == 1

    @pytest.mark.asyncio
    async def test_crypto_new_timestamp_processed(self, mock_stream_with_crypto):
        """Test that crypto bars with new timestamp are processed."""
        btc_bar1 = MagicMock()
        btc_bar1.timestamp = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        btc_bar1.close = 45000.0
        btc_bar1.open = 44800.0
        btc_bar1.high = 45100.0
        btc_bar1.low = 44700.0
        btc_bar1.volume = 1000000

        btc_bar2 = MagicMock()
        btc_bar2.timestamp = datetime(2024, 1, 1, 12, 1, 0, tzinfo=timezone.utc)
        btc_bar2.close = 45100.0
        btc_bar2.open = 45000.0
        btc_bar2.high = 45200.0
        btc_bar2.low = 44950.0
        btc_bar2.volume = 900000

        processed = []
        mock_stream_with_crypto.on_bar = AsyncMock(
            side_effect=lambda bar: processed.append(bar.symbol)
        )

        # First poll
        mock_response1 = MagicMock()
        mock_response1.data = {"BTC/USD": [btc_bar1]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response1
        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Second poll with new timestamp
        mock_response2 = MagicMock()
        mock_response2.data = {"BTC/USD": [btc_bar2]}
        mock_stream_with_crypto.crypto_client.get_crypto_bars.return_value = mock_response2
        await mock_stream_with_crypto._poll_crypto_batch(["BTC/USD"])

        # Both bars should be processed
        assert len(processed) == 2
