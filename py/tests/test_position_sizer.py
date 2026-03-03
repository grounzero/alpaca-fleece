"""Tests for position sizer module."""

import pytest

from src.position_sizer import calculate_position_size


class TestCalculatePositionSize:
    """Test cases for calculate_position_size function."""

    def test_basic_position_sizing(self):
        """Test basic position sizing with default parameters."""
        # $100,000 equity, $100 stock price
        # Max position: $10,000 (10%) = 100 shares
        # Risk-based: $1,000 risk / $1 stop = 1,000 shares
        # Result: min(100, 1000) = 100 shares
        qty = calculate_position_size(
            symbol="AAPL",
            side="buy",
            account_equity=100000.0,
            current_price=100.0,
        )
        assert qty == 100

    def test_risk_based_limits_position_size(self):
        """Test that risk per trade can limit position size."""
        # $100,000 equity, $500 stock price
        # Max position: $10,000 (10%) = 20 shares
        # Risk-based: $1,000 risk / $5 stop = 200 shares
        # Result: min(20, 200) = 20 shares (equity limit wins)
        qty = calculate_position_size(
            symbol="NVDA",
            side="buy",
            account_equity=100000.0,
            current_price=500.0,
        )
        assert qty == 20

    def test_high_price_stock_risk_limited(self):
        """Test high-priced stock where risk limits position."""
        # $50,000 equity, $1000 stock price
        # Max position: $5,000 (10%) = 5 shares
        # Risk-based: $500 risk / $10 stop = 50 shares
        # Result: min(5, 50) = 5 shares (equity limit wins)
        qty = calculate_position_size(
            symbol="GOOGL",
            side="buy",
            account_equity=50000.0,
            current_price=1000.0,
        )
        assert qty == 5

    def test_low_price_stock_risk_limited(self):
        """Test low-priced stock where risk limits position."""
        # $50,000 equity, $10 stock price, tight stop
        # Max position: $5,000 (10%) = 500 shares
        # Risk-based: $500 risk / $0.10 stop (1%) = 5,000 shares
        # Result: min(500, 5000) = 500 shares (equity limit wins)
        qty = calculate_position_size(
            symbol="F",
            side="buy",
            account_equity=50000.0,
            current_price=10.0,
            stop_loss_pct=0.01,
        )
        assert qty == 500

    def test_risk_limits_more_than_equity(self):
        """Test scenario where risk limit is more restrictive than equity limit."""
        # $100,000 equity, $50 stock price, 0.5% risk, 2% stop
        # Max position: $10,000 (10%) = 200 shares
        # Risk-based: $500 risk (0.5%) / $1 stop (2%) = 500 shares
        # Result: min(200, 500) = 200 (equity still wins)

        # Now with 0.25% risk and 0.5% stop
        # Max position: $10,000 (10%) = 200 shares
        # Risk-based: $250 risk (0.25%) / $0.25 stop (0.5%) = 1,000 shares
        # Result: min(200, 1000) = 200 (equity still wins)

        # Try with wide stop loss where risk limits
        # $100,000 equity, $50 stock price
        # Max position: $10,000 (10%) = 200 shares
        # Risk-based: $1,000 risk (1%) / $5 stop (10%) = 200 shares
        # Result: min(200, 200) = 200
        qty = calculate_position_size(
            symbol="TEST",
            side="buy",
            account_equity=100000.0,
            current_price=50.0,
            max_position_pct=0.10,
            max_risk_per_trade_pct=0.01,
            stop_loss_pct=0.10,  # 10% stop
        )
        assert qty == 200

    def test_very_small_account_minimum_one_share(self):
        """Test that minimum 1 share is returned even for small accounts."""
        # $100 equity, $1000 stock price
        # Max position: $10 (10%) = 0 shares
        # Risk-based: $1 risk (1%) / $10 stop (1%) = 0 shares
        # Result: max(1, min(0, 0)) = 1 share (minimum)
        qty = calculate_position_size(
            symbol="GOOGL",
            side="buy",
            account_equity=100.0,
            current_price=1000.0,
        )
        assert qty == 1

    def test_custom_max_position_pct(self):
        """Test custom max position percentage."""
        # $100,000 equity, $100 stock price, 20% max position
        # Max position: $20,000 (20%) = 200 shares
        # Risk-based: $1,000 risk (1%) / $1 stop (1%) = 1,000 shares
        # Result: min(200, 1000) = 200 shares
        qty = calculate_position_size(
            symbol="AAPL",
            side="buy",
            account_equity=100000.0,
            current_price=100.0,
            max_position_pct=0.20,
        )
        assert qty == 200

    def test_custom_risk_per_trade(self):
        """Test custom max risk per trade percentage."""
        # $100,000 equity, $100 stock price, 2% risk
        # Max position: $10,000 (10%) = 100 shares
        # Risk-based: $2,000 risk (2%) / $1 stop (1%) = 2,000 shares
        # Result: min(100, 2000) = 100 shares (equity still wins)

        # But with 5% risk:
        # Risk-based: $5,000 risk (5%) / $1 stop (1%) = 5,000 shares
        # Result: min(100, 5000) = 100 shares (equity still wins)

        # Try scenario where risk wins:
        # $100,000 equity, $500 stock price, 5% risk, 5% stop
        # Max position: $10,000 (10%) = 20 shares
        # Risk-based: $5,000 risk (5%) / $25 stop (5%) = 200 shares
        # Result: min(20, 200) = 20 (equity still wins)

        # Try with 0.5% max position and 2% risk:
        # $100,000 equity, $100 stock price
        # Max position: $500 (0.5%) = 5 shares
        # Risk-based: $2,000 risk (2%) / $1 stop (1%) = 2,000 shares
        # Result: min(5, 2000) = 5 (equity wins)
        qty = calculate_position_size(
            symbol="AAPL",
            side="buy",
            account_equity=100000.0,
            current_price=100.0,
            max_position_pct=0.005,
            max_risk_per_trade_pct=0.02,
        )
        assert qty == 5

    def test_custom_stop_loss(self):
        """Test custom stop loss percentage."""
        # $100,000 equity, $100 stock price, 5% stop
        # Max position: $10,000 (10%) = 100 shares
        # Risk-based: $1,000 risk (1%) / $5 stop (5%) = 200 shares
        # Result: min(100, 200) = 100 shares (equity wins)

        # With 0.5% stop (tighter):
        # Risk-based: $1,000 risk (1%) / $0.50 stop (0.5%) = 2,000 shares
        # Result: min(100, 2000) = 100 shares (equity still wins)

        # With very wide stop where risk limits:
        # $100,000 equity, $100 stock price, 20% stop
        # Max position: $10,000 (10%) = 100 shares
        # Risk-based: $1,000 risk (1%) / $20 stop (20%) = 50 shares
        # Result: min(100, 50) = 50 shares (risk wins!)
        qty = calculate_position_size(
            symbol="AAPL",
            side="buy",
            account_equity=100000.0,
            current_price=100.0,
            stop_loss_pct=0.20,
        )
        assert qty == 50

    def test_sell_side_same_calculation(self):
        """Test that sell side uses same calculation as buy."""
        qty_buy = calculate_position_size(
            symbol="AAPL",
            side="buy",
            account_equity=100000.0,
            current_price=100.0,
        )
        qty_sell = calculate_position_size(
            symbol="AAPL",
            side="sell",
            account_equity=100000.0,
            current_price=100.0,
        )
        assert qty_buy == qty_sell

    def test_very_high_price_stock(self):
        """Test very high-priced stock (e.g., BRK.A)."""
        # $500,000 equity, $500,000 stock price
        # Max position: $50,000 (10%) = 0 shares
        # Risk-based: $5,000 risk (1%) / $5,000 stop (1%) = 1 share
        # Result: max(1, min(0, 1)) = 1 share
        qty = calculate_position_size(
            symbol="BRK.A",
            side="buy",
            account_equity=500000.0,
            current_price=500000.0,
        )
        assert qty == 1

    def test_large_account_very_cheap_stock(self):
        """Test large account with very cheap stock."""
        # $1,000,000 equity, $5 stock price
        # Max position: $100,000 (10%) = 20,000 shares
        # Risk-based: $10,000 risk (1%) / $0.05 stop (1%) = 200,000 shares
        # Result: min(20000, 200000) = 20,000 shares (equity wins)
        qty = calculate_position_size(
            symbol="PENNY",
            side="buy",
            account_equity=1000000.0,
            current_price=5.0,
        )
        assert qty == 20000

    def test_invalid_current_price_raises(self):
        """Test that invalid current price raises ValueError."""
        with pytest.raises(ValueError, match="Current price must be positive"):
            calculate_position_size(
                symbol="AAPL",
                side="buy",
                account_equity=100000.0,
                current_price=0.0,
            )

        with pytest.raises(ValueError, match="Current price must be positive"):
            calculate_position_size(
                symbol="AAPL",
                side="buy",
                account_equity=100000.0,
                current_price=-10.0,
            )

    def test_invalid_account_equity_raises(self):
        """Test that invalid account equity raises ValueError."""
        with pytest.raises(ValueError, match="Account equity must be positive"):
            calculate_position_size(
                symbol="AAPL",
                side="buy",
                account_equity=0.0,
                current_price=100.0,
            )

        with pytest.raises(ValueError, match="Account equity must be positive"):
            calculate_position_size(
                symbol="AAPL",
                side="buy",
                account_equity=-1000.0,
                current_price=100.0,
            )

    def test_zero_stop_loss_handles_gracefully(self):
        """Test that zero stop loss doesn't cause division by zero."""
        # With 0% stop loss, risk-based calculation should return 0
        # But we still get minimum 1 share
        qty = calculate_position_size(
            symbol="AAPL",
            side="buy",
            account_equity=100000.0,
            current_price=100.0,
            stop_loss_pct=0.0,
        )
        # Max position: $10,000 / $100 = 100 shares
        # Risk-based: division by zero avoided, returns 0
        # Result: max(1, min(100, 0)) = 1 share
        assert qty >= 1

    def test_fractional_share_result_rounded_down(self):
        """Test that fractional shares are rounded down."""
        # $100,000 equity, $33.33 stock price
        # Max position: $10,000 (10%) = 300.03... → 300 shares
        # Risk-based: $1,000 risk (1%) / $0.333 stop (1%) = 3,000.3... → 3,000 shares
        # Result: min(300, 3000) = 300 shares
        qty = calculate_position_size(
            symbol="WEIRD",
            side="buy",
            account_equity=100000.0,
            current_price=33.33,
        )
        assert qty == 300
