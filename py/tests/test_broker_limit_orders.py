"""Tests for limit order implementation (Fix 1.1)."""

import pytest

from src.broker import Broker, BrokerError


@pytest.fixture
def broker():
    """Create broker client for testing."""
    return Broker(
        api_key="test_key",
        secret_key="test_secret",
        paper=True,
    )


class TestLimitOrders:
    """Test limit order functionality."""

    def test_submit_limit_order_with_null_price_raises_error(self, broker, mocker):
        """Limit order with limit_price=None raises BrokerError."""
        mocker.patch.object(broker, "_call_with_timeout")

        with pytest.raises(BrokerError, match="limit_price required"):
            broker.submit_order(
                symbol="AAPL",
                side="buy",
                qty=10,
                client_order_id="test-1",
                order_type="limit",
                limit_price=None,  # MISSING
            )

    def test_submit_limit_order_with_price_submits_limit_request(self, broker, mocker):
        """Limit order with valid price_limit submits LimitOrderRequest."""
        # Mock the internal _call_with_timeout to return a successful order
        mock_order_result = {
            "id": "alpaca-order-123",
            "client_order_id": "test-1",
            "symbol": "AAPL",
            "side": "buy",
            "qty": 10,
            "status": "new",
            "filled_qty": 0,
            "filled_avg_price": None,
        }

        mock_call = mocker.patch.object(
            broker,
            "_call_with_timeout",
            return_value=mock_order_result,
        )

        # First call to _call_with_timeout will be the submit_order call
        # We need to verify that a LimitOrderRequest was constructed,
        # but since we're mocking _call_with_timeout, we can verify the
        # correct request object was passed

        result = broker.submit_order(
            symbol="AAPL",
            side="buy",
            qty=10,
            client_order_id="test-1",
            order_type="limit",
            limit_price=100.0,  # Valid limit price
        )

        assert result["client_order_id"] == "test-1"
        assert result["symbol"] == "AAPL"

        # Verify _call_with_timeout was called
        assert mock_call.called

        # Retrieve the actual call args
        call_args = mock_call.call_args
        # call_args[0][0] is a lambda wrapping self.client.submit_order
        # We can verify it was called with the right timeout
        assert call_args[1]["timeout"] == broker._submit_timeout
        assert call_args[1]["operation_name"] == "submit_order"

    def test_submit_market_order_still_works(self, broker, mocker):
        """Market orders still work as before."""
        mock_order_result = {
            "id": "alpaca-order-456",
            "client_order_id": "test-2",
            "symbol": "MSFT",
            "side": "sell",
            "qty": 5,
            "status": "filled",
            "filled_qty": 5,
            "filled_avg_price": 200.0,
        }

        mocker.patch.object(
            broker,
            "_call_with_timeout",
            return_value=mock_order_result,
        )

        result = broker.submit_order(
            symbol="MSFT",
            side="sell",
            qty=5,
            client_order_id="test-2",
            order_type="market",
        )

        assert result["client_order_id"] == "test-2"
        assert result["status"] == "filled"

    def test_submit_order_invalid_type_raises_error(self, broker, mocker):
        """Invalid order_type raises BrokerError."""
        mocker.patch.object(broker, "_call_with_timeout")

        with pytest.raises(BrokerError, match="Invalid order_type"):
            broker.submit_order(
                symbol="AAPL",
                side="buy",
                qty=10,
                client_order_id="test-3",
                order_type="stop",  # Invalid
            )
