"""Tests for automatic feed fallback behavior in StreamPolling.

Tests cover:
- SIP config without subscription falls back to IEX
- SIP config with valid subscription works normally
- IEX config skips validation
- Non-subscription errors are raised, not caught
- Runtime fallback behavior in _poll_batch
"""

from datetime import datetime, timezone
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from alpaca.data.enums import DataFeed
from alpaca.data.requests import StockBarsRequest

from src.stream_polling import StreamPolling


class TestFeedFallback:
    """Test automatic feed fallback behavior."""

    @pytest.fixture
    def mock_stream_sip(self):
        """Create a StreamPolling instance with SIP feed and mocked client."""
        with patch("src.stream_polling.StockHistoricalDataClient"):
            stream = StreamPolling("api_key", "secret_key", feed="sip")
            stream.client = MagicMock()
            return stream

    @pytest.fixture
    def mock_stream_iex(self):
        """Create a StreamPolling instance with IEX feed and mocked client."""
        with patch("src.stream_polling.StockHistoricalDataClient"):
            stream = StreamPolling("api_key", "secret_key", feed="iex")
            stream.client = MagicMock()
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
    async def test_sip_without_subscription_fallback(self, mock_stream_sip, caplog):
        """SIP config + subscription error → falls back to IEX."""
        # Mock: SIP returns error with "subscription does not permit" message
        mock_stream_sip.client.get_stock_bars.side_effect = Exception(
            "subscription does not permit access"
        )

        with caplog.at_level("WARNING"):
            await mock_stream_sip._validate_feed()

        # Assert: _use_fallback flag set to True
        assert mock_stream_sip._use_fallback is True

        # Assert: Warning logged with suppression hint
        assert "SIP feed requires subscription" in caplog.text
        assert "falling back to IEX" in caplog.text
        assert "stream_feed: iex in config/trading.yaml" in caplog.text

    @pytest.mark.asyncio
    async def test_sip_with_subscription_works(self, mock_stream_sip, sample_bar, caplog):
        """SIP config + valid subscription → uses SIP."""
        # Mock: SIP returns valid bars
        mock_response = MagicMock()
        mock_response.data = {"AAPL": [sample_bar]}
        mock_stream_sip.client.get_stock_bars.return_value = mock_response

        with caplog.at_level("INFO"):
            await mock_stream_sip._validate_feed()

        # Assert: _use_fallback flag is False
        assert mock_stream_sip._use_fallback is False

        # Assert: No warning logged (should have INFO log for SIP feed)
        assert "Using SIP feed" in caplog.text
        assert not any(record.levelname == "WARNING" for record in caplog.records)

    @pytest.mark.asyncio
    async def test_iex_config_no_test(self, mock_stream_iex, caplog):
        """IEX config → no validation call made."""
        with caplog.at_level("INFO"):
            await mock_stream_iex._validate_feed()

        # Assert: _use_fallback flag is False
        assert mock_stream_iex._use_fallback is False

        # Assert: No API call was made for validation
        mock_stream_iex.client.get_stock_bars.assert_not_called()

        # Assert: Info log: "Using IEX feed"
        assert "Using IEX feed" in caplog.text

    @pytest.mark.asyncio
    async def test_non_subscription_error_raises(self, mock_stream_sip):
        """API error (not subscription) → raises normally."""
        # Mock: SIP returns 500 server error
        mock_stream_sip.client.get_stock_bars.side_effect = Exception("Internal Server Error")

        # Assert: Exception raised, not caught as fallback
        with pytest.raises(Exception, match="Internal Server Error"):
            await mock_stream_sip._validate_feed()

        # Fallback flag should remain False since we raised
        assert mock_stream_sip._use_fallback is False

    @pytest.mark.asyncio
    async def test_runtime_uses_fallback_when_set(self, mock_stream_sip, sample_bar, caplog):
        """_poll_batch uses IEX when _use_fallback is True."""
        # Set _use_fallback = True
        mock_stream_sip._use_fallback = True

        mock_response = MagicMock()
        mock_response.data = {"AAPL": [sample_bar]}
        mock_stream_sip.client.get_stock_bars.return_value = mock_response

        mock_stream_sip.on_bar = AsyncMock()

        with caplog.at_level("INFO"):
            await mock_stream_sip._poll_batch(["AAPL"])

        # Assert: All requests use feed="iex" regardless of config
        call_args = mock_stream_sip.client.get_stock_bars.call_args
        request = call_args[0][0]
        assert isinstance(request, StockBarsRequest)
        assert request.feed == DataFeed.IEX

        # Assert: Info log about fallback
        assert "Using IEX fallback feed (SIP unavailable)" in caplog.text

    @pytest.mark.asyncio
    async def test_runtime_uses_config_when_no_fallback(self, mock_stream_sip, sample_bar):
        """_poll_batch uses config feed when no fallback needed."""
        # Set _use_fallback = False, config = "sip"
        mock_stream_sip._use_fallback = False

        mock_response = MagicMock()
        mock_response.data = {"AAPL": [sample_bar]}
        mock_stream_sip.client.get_stock_bars.return_value = mock_response

        mock_stream_sip.on_bar = AsyncMock()

        await mock_stream_sip._poll_batch(["AAPL"])

        # Assert: Requests use feed="sip"
        call_args = mock_stream_sip.client.get_stock_bars.call_args
        request = call_args[0][0]
        assert isinstance(request, StockBarsRequest)
        assert request.feed == DataFeed.SIP

    @pytest.mark.asyncio
    async def test_fallback_logged_only_once(self, mock_stream_sip, sample_bar, caplog):
        """Fallback message is logged only once per session."""
        mock_stream_sip._use_fallback = True
        mock_stream_sip._fallback_logged = False

        mock_response = MagicMock()
        mock_response.data = {"AAPL": [sample_bar]}
        mock_stream_sip.client.get_stock_bars.return_value = mock_response

        mock_stream_sip.on_bar = AsyncMock()

        with caplog.at_level("INFO"):
            # First call should log
            await mock_stream_sip._poll_batch(["AAPL"])
            # Second call should not log again
            await mock_stream_sip._poll_batch(["AAPL"])

        # Should only see the fallback message once
        assert caplog.text.count("Using IEX fallback feed") == 1

    @pytest.mark.asyncio
    async def test_start_calls_validate_feed(self, mock_stream_iex):
        """start() method calls _validate_feed."""
        mock_stream_iex._validate_feed = AsyncMock()
        # Prevent starting the real infinite polling loop during the test
        mock_stream_iex._poll_loop = AsyncMock()

        await mock_stream_iex.start(["AAPL", "MSFT"])

        # Assert _validate_feed was called
        mock_stream_iex._validate_feed.assert_called_once()

        # Clean up
        await mock_stream_iex.stop()

    @pytest.mark.asyncio
    async def test_subscription_error_variations(self, mock_stream_sip):
        """Test various subscription error message formats."""
        test_cases = [
            "subscription does not permit access",
            "Your subscription does not permit",
            "subscription does not permit",
        ]

        for error_msg in test_cases:
            mock_stream_sip.client.get_stock_bars.side_effect = Exception(error_msg)
            mock_stream_sip._use_fallback = False

            await mock_stream_sip._validate_feed()

            assert mock_stream_sip._use_fallback is True, f"Failed for: {error_msg}"

    @pytest.mark.asyncio
    async def test_iex_config_uppercase(self, mock_stream_iex, caplog):
        """IEX config (uppercase) → no validation call made."""
        mock_stream_iex.feed = "iex"
        mock_stream_iex._data_feed = DataFeed.IEX

        with caplog.at_level("INFO"):
            await mock_stream_iex._validate_feed()

        # Assert: _use_fallback flag is False
        assert mock_stream_iex._use_fallback is False

        # Assert: No API call was made for validation
        mock_stream_iex.client.get_stock_bars.assert_not_called()
