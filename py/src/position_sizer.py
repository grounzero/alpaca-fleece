"""Position sizing module for risk-based position calculation.

This module provides position sizing calculations based on:
- Account equity
- Maximum position percentage (risk per position)
- Maximum risk per trade percentage
- Stop loss percentage
"""

import logging

logger = logging.getLogger(__name__)


def calculate_position_size(
    symbol: str,
    side: str,
    account_equity: float,
    current_price: float,
    max_position_pct: float = 0.10,  # 10% of equity per position
    max_risk_per_trade_pct: float = 0.01,  # 1% risk per trade
    stop_loss_pct: float = 0.01,  # 1% stop loss
) -> int:
    """Calculate position size based on risk parameters.

    Uses the smaller of:
    - Max position size (% of equity)
    - Risk-based size (risk $ / stop distance)

    Args:
        symbol: Stock symbol (for logging)
        side: Trade side ("buy" or "sell")
        account_equity: Total account equity
        current_price: Current market price
        max_position_pct: Maximum position size as % of equity (default 10%)
        max_risk_per_trade_pct: Maximum risk per trade as % of equity (default 1%)
        stop_loss_pct: Stop loss percentage from entry (default 1%)

    Returns:
        Number of shares to trade (minimum 1)

    Raises:
        ValueError: If current_price <= 0 or account_equity <= 0
    """
    if current_price <= 0:
        raise ValueError(f"Current price must be positive, got {current_price}")

    if account_equity <= 0:
        raise ValueError(f"Account equity must be positive, got {account_equity}")

    # Max position size by equity
    max_position_value = account_equity * max_position_pct
    max_shares_by_equity = int(max_position_value / current_price)

    # Risk-based position size
    risk_per_trade = account_equity * max_risk_per_trade_pct
    stop_distance = current_price * stop_loss_pct
    max_shares_by_risk = int(risk_per_trade / stop_distance) if stop_distance > 0 else 0

    # Take the smaller (more conservative), minimum 1 share
    qty = max(1, min(max_shares_by_equity, max_shares_by_risk))

    logger.debug(
        f"Position size for {symbol} ({side}): "
        f"qty={qty}, price=${current_price:.2f}, "
        f"equity=${account_equity:.2f}, "
        f"max_by_equity={max_shares_by_equity}, max_by_risk={max_shares_by_risk}"
    )

    return qty
