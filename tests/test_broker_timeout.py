"""Unit tests for `Broker` timeout and related executor behavior."""

import time
from unittest.mock import MagicMock, patch

import pytest

from src.broker import Broker, BrokerError


class TestBrokerTimeout:
    """Test broker calls with timeout protection."""

    def test_query_timeout_is_configurable(self):
        """Test that query timeout can be configured."""
        with patch("src.broker.TradingClient"):
            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                query_timeout=5.0,
                submit_timeout=20.0,
            )
            assert broker._default_timeout == 5.0
            assert broker._submit_timeout == 20.0

    def test_default_timeouts_set_correctly(self):
        """Test default timeouts are 10s for queries, 15s for submission."""
        with patch("src.broker.TradingClient"):
            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
            )
            assert broker._default_timeout == 10.0
            assert broker._submit_timeout == 15.0

    def test_get_account_raises_broker_error_on_timeout(self):
        """Test get_account raises BrokerError on timeout."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            # Simulate a slow response
            def slow_call():
                time.sleep(2)
                return MagicMock()

            mock_client.get_account = slow_call

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                query_timeout=0.1,  # Very short timeout for testing
            )

            with pytest.raises(BrokerError, match="timed out"):
                broker.get_account()

    def test_get_positions_raises_broker_error_on_timeout(self):
        """Test get_positions raises BrokerError on timeout."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            def slow_call():
                time.sleep(2)
                return []

            mock_client.get_all_positions = slow_call

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                query_timeout=0.1,
            )

            with pytest.raises(BrokerError, match="timed out"):
                broker.get_positions()

    def test_get_open_orders_raises_broker_error_on_timeout(self):
        """Test get_open_orders raises BrokerError on timeout."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            def slow_call():
                time.sleep(2)
                return []

            mock_client.get_orders = slow_call

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                query_timeout=0.1,
            )

            with pytest.raises(BrokerError, match="timed out"):
                broker.get_open_orders()

    def test_get_clock_raises_broker_error_on_timeout(self):
        """Test get_clock raises BrokerError on timeout."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            def slow_call():
                time.sleep(2)
                return MagicMock()

            mock_client.get_clock = slow_call

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                query_timeout=0.1,
            )

            with pytest.raises(BrokerError, match="timed out"):
                broker.get_clock()

    def test_submit_order_raises_broker_error_on_timeout(self):
        """Test submit_order raises BrokerError on timeout."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            def slow_call(*args, **kwargs):
                time.sleep(2)
                return MagicMock()

            mock_client.submit_order = slow_call

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                submit_timeout=0.1,
            )

            with pytest.raises(BrokerError, match="timed out"):
                broker.submit_order(
                    symbol="AAPL",
                    side="buy",
                    qty=1.0,
                    client_order_id="test-123",
                )

    def test_cancel_order_raises_broker_error_on_timeout(self):
        """Test cancel_order raises BrokerError on timeout."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            def slow_call(*args, **kwargs):
                time.sleep(2)
                return None

            mock_client.cancel_order_by_id = slow_call

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                submit_timeout=0.1,
            )

            with pytest.raises(BrokerError, match="timed out"):
                broker.cancel_order("order-123")

    def test_timeout_message_includes_operation_name(self):
        """Test timeout error message includes the operation name."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            def slow_call():
                time.sleep(2)
                return MagicMock()

            mock_client.get_account = slow_call

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                query_timeout=0.1,
            )

            with pytest.raises(BrokerError, match="get_account"):
                broker.get_account()

    def test_successful_call_returns_result(self):
        """Test that successful calls return expected results."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            mock_account = MagicMock()
            mock_account.equity = "10000.00"
            mock_account.buying_power = "5000.00"
            mock_account.cash = "2000.00"
            mock_account.portfolio_value = "10000.00"
            mock_client.get_account.return_value = mock_account

            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
            )

            result = broker.get_account()

            assert result["equity"] == 10000.00
            assert result["buying_power"] == 5000.00

    def test_submit_order_uses_submit_timeout_not_query_timeout(self):
        """Test that submit_order uses the longer submit_timeout."""
        with patch("src.broker.TradingClient") as MockClient:
            mock_client = MagicMock()
            MockClient.return_value = mock_client

            def slow_call(*args, **kwargs):
                time.sleep(0.3)
                return MagicMock()

            mock_client.submit_order = slow_call

            # query_timeout is 0.1s, submit_timeout is 0.5s
            # submit should succeed because it uses submit_timeout
            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
                query_timeout=0.1,
                submit_timeout=0.5,
            )

            # Should not timeout because submit_timeout (0.5s) > sleep (0.3s)
            result = broker.submit_order(
                symbol="AAPL",
                side="buy",
                qty=1.0,
                client_order_id="test-123",
            )

            assert result is not None

    def test_executor_shutdown_on_cleanup(self):
        """Test that ThreadPoolExecutor is properly shutdown during cleanup."""
        with patch("src.broker.TradingClient"):
            broker = Broker(
                api_key="test_key",
                secret_key="test_secret",
                paper=True,
            )
            assert broker._executor is not None
            # Clean up
            broker._executor.shutdown(wait=False)
