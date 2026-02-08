"""Tests for broker retry logic with exponential backoff."""

import time
from unittest.mock import MagicMock, patch

import pytest

from src.broker import Broker, BrokerError


@pytest.fixture
def mock_broker():
    """Create a broker with mocked client and patched sleep."""
    with patch("src.broker.TradingClient"):
        with patch("src.broker.time.sleep"):  # Prevent actual sleeping in tests
            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
            )
            broker.client = MagicMock()
            yield broker
            broker.close()  # Clean up executor


class TestRetryWithBackoff:
    """Test suite for _retry_with_backoff method."""

    def test_success_on_first_attempt(self, mock_broker):
        """Test that successful call returns immediately without retries."""
        mock_result = {"equity": "100000", "buying_power": "200000"}
        mock_broker.client.get_account.return_value = mock_result

        result = mock_broker.get_account()

        assert result["equity"] == 100000.0
        assert mock_broker.client.get_account.call_count == 1

    def test_success_after_two_failures(self, mock_broker):
        """Test that retry succeeds after initial failures."""
        mock_result = {"equity": "100000", "buying_power": "200000"}

        # Fail twice, then succeed
        call_count = 0

        def side_effect():
            nonlocal call_count
            call_count += 1
            if call_count <= 2:
                raise Exception("Network error")
            return mock_result

        mock_broker.client.get_account.side_effect = side_effect

        result = mock_broker.get_account()

        assert result["equity"] == 100000.0
        assert call_count == 3  # 2 failures + 1 success

    def test_failure_after_max_retries(self, mock_broker):
        """Test that max retries exceeded raises BrokerError."""
        mock_broker.client.get_account.side_effect = Exception("Persistent network error")

        with pytest.raises(BrokerError) as exc_info:
            mock_broker.get_account()

        assert "failed after 3 attempts" in str(exc_info.value)
        assert mock_broker.client.get_account.call_count == 3

    def test_exponential_backoff_timing(self, mock_broker):
        """Test that delays follow exponential backoff pattern."""
        mock_broker.client.get_account.side_effect = Exception("Network error")

        delays = []

        def mock_sleep(seconds):
            delays.append(seconds)
            # Don't actually sleep in tests

        # Patch sleep to capture delays but not actually sleep
        with patch("src.broker.time.sleep", side_effect=mock_sleep):
            with pytest.raises(BrokerError):
                mock_broker.get_account()

        # Should have 2 delays: 1.0s and 2.0s (base_delay * 2^0, base_delay * 2^1)
        assert len(delays) == 2
        assert delays[0] == 1.0  # First retry: 1.0 * 2^0
        assert delays[1] == 2.0  # Second retry: 1.0 * 2^1

    def test_custom_max_retries(self, mock_broker):
        """Test that custom max_retries parameter works."""
        mock_broker.client.get_account.side_effect = Exception("Network error")

        with pytest.raises(BrokerError):
            mock_broker._retry_with_backoff(
                mock_broker.client.get_account,
                max_retries=5,
                operation_name="test_operation",
            )

        assert mock_broker.client.get_account.call_count == 5

    def test_custom_base_delay(self, mock_broker):
        """Test that custom base_delay parameter works."""
        mock_broker.client.get_account.side_effect = Exception("Network error")

        delays = []

        def mock_sleep(seconds):
            delays.append(seconds)

        # Patch sleep to capture delays
        with patch("src.broker.time.sleep", side_effect=mock_sleep):
            with pytest.raises(BrokerError):
                mock_broker._retry_with_backoff(
                    mock_broker.client.get_account,
                    base_delay=0.5,
                    operation_name="test_operation",
                )

        # With base_delay=0.5: 0.5 * 2^0 = 0.5, 0.5 * 2^1 = 1.0
        assert delays[0] == 0.5
        assert delays[1] == 1.0

    def test_broker_error_wrapped_correctly(self, mock_broker):
        """Test that BrokerError is raised with correct message."""
        mock_broker.client.get_account.side_effect = Exception("Connection timeout")

        with pytest.raises(BrokerError) as exc_info:
            mock_broker.get_account()

        assert "get_account failed after 3 attempts" in str(exc_info.value)
        assert "Connection timeout" in str(exc_info.value)


class TestRetryAppliedToMethods:
    """Test that retry is applied to correct methods."""

    def test_get_account_uses_retry(self, mock_broker):
        """Test that get_account uses retry logic."""
        mock_result = MagicMock()
        mock_result.equity = "100000"
        mock_result.buying_power = "200000"
        mock_result.cash = "50000"
        mock_result.portfolio_value = "100000"

        call_count = 0

        def side_effect():
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                raise Exception("Transient error")
            return mock_result

        mock_broker.client.get_account.side_effect = side_effect

        result = mock_broker.get_account()

        assert result["equity"] == 100000.0
        assert call_count == 2  # 1 failure + 1 success

    def test_get_positions_uses_retry(self, mock_broker):
        """Test that get_positions uses retry logic."""
        mock_position = MagicMock()
        mock_position.symbol = "AAPL"
        mock_position.qty = "10"
        mock_position.avg_entry_price = "150.00"
        mock_position.current_price = "155.00"

        call_count = 0

        def side_effect():
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                raise Exception("Transient error")
            return [mock_position]

        mock_broker.client.get_all_positions.side_effect = side_effect

        result = mock_broker.get_positions()

        assert len(result) == 1
        assert result[0]["symbol"] == "AAPL"
        assert call_count == 2

    def test_get_open_orders_uses_retry(self, mock_broker):
        """Test that get_open_orders uses retry logic."""
        mock_order = MagicMock()
        mock_order.id = "order-123"
        mock_order.client_order_id = "client-123"
        mock_order.symbol = "AAPL"
        mock_order.side.value = "buy"
        mock_order.qty = "10"
        mock_order.status.value = "new"
        mock_order.filled_qty = "0"
        mock_order.filled_avg_price = None
        mock_order.created_at = None

        call_count = 0

        def side_effect():
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                raise Exception("Transient error")
            return [mock_order]

        mock_broker.client.get_orders.side_effect = side_effect

        result = mock_broker.get_open_orders()

        assert len(result) == 1
        assert result[0]["symbol"] == "AAPL"
        assert call_count == 2

    def test_get_clock_uses_retry(self, mock_broker):
        """Test that get_clock uses retry logic."""
        mock_clock = MagicMock()
        mock_clock.is_open = True
        mock_clock.next_open = None
        mock_clock.next_close = None
        mock_clock.timestamp = None

        call_count = 0

        def side_effect():
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                raise Exception("Transient error")
            return mock_clock

        mock_broker.client.get_clock.side_effect = side_effect

        result = mock_broker.get_clock()

        assert result["is_open"] is True
        assert call_count == 2


class TestNoRetryOnMutatingOperations:
    """Test that retry is NOT applied to mutating operations."""

    def test_submit_order_does_not_use_retry(self, mock_broker):
        """Test that submit_order does NOT use retry logic."""
        mock_broker.client.submit_order.side_effect = Exception("Should fail immediately")

        with pytest.raises(BrokerError):
            mock_broker.submit_order(
                symbol="AAPL",
                side="buy",
                qty=10,
                client_order_id="test-123",
            )

        # Should only be called once - no retries
        assert mock_broker.client.submit_order.call_count == 1

    def test_cancel_order_does_not_use_retry(self, mock_broker):
        """Test that cancel_order does NOT use retry logic."""
        mock_broker.client.cancel_order_by_id.side_effect = Exception("Should fail immediately")

        with pytest.raises(BrokerError):
            mock_broker.cancel_order("order-123")

        # Should only be called once - no retries
        assert mock_broker.client.cancel_order_by_id.call_count == 1
