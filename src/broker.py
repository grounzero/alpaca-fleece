"""Broker execution layer - EXECUTION ENDPOINTS ONLY.

This module owns:
- /v2/account (get account info)
- /v2/positions (get current positions)
- /v2/orders (submit, cancel, get orders)
- /v2/clock (trading hours gate)

MUST NOT contain:
- Market data endpoints (/v2/stocks/bars, /v2/stocks/snapshots)
- Reference endpoints (/v2/assets, /v2/watchlists, /v2/calendar)

Clock (/v2/clock) is owned EXCLUSIVELY by this module.
Calendar (/v2/calendar) is NOT owned here; use alpaca_api.calendar instead.
"""

from typing import Optional, TypedDict

from alpaca.trading.client import TradingClient
from alpaca.trading.enums import OrderSide, TimeInForce
from alpaca.trading.requests import MarketOrderRequest


class AccountInfo(TypedDict, total=False):
    """Account information from broker."""

    equity: float
    buying_power: float
    cash: float
    portfolio_value: float


class PositionInfo(TypedDict, total=False):
    """Position information."""

    symbol: str
    qty: float
    avg_entry_price: Optional[float]
    current_price: Optional[float]


class OrderInfo(TypedDict, total=False):
    """Order information."""

    id: str
    client_order_id: str
    symbol: str
    side: Optional[str]
    qty: Optional[float]
    status: Optional[str]
    filled_qty: float
    filled_avg_price: Optional[float]
    created_at: Optional[str]


class ClockInfo(TypedDict, total=False):
    """Market clock information."""

    is_open: bool
    next_open: Optional[str]
    next_close: Optional[str]
    timestamp: Optional[str]


class BrokerError(Exception):
    """Raised when broker operation fails."""

    pass


class Broker:
    """Execution-only broker client."""

    def __init__(self, api_key: str, secret_key: str, paper: bool = True) -> None:
        """Initialise broker client.

        Args:
            api_key: Alpaca API key
            secret_key: Alpaca secret key
            paper: True for paper trading, False for live
        """
        self.paper: bool = paper
        self.client: TradingClient = TradingClient(
            api_key=api_key,
            secret_key=secret_key,
            paper=paper,
        )

    def get_account(self) -> AccountInfo:
        """Get account info (equity, buying_power, etc).

        Returns:
            Dict with keys: equity, buying_power, cash, etc
        """
        try:
            account = self.client.get_account()
            return {
                "equity": float(account.equity),
                "buying_power": float(account.buying_power),
                "cash": float(account.cash),
                "portfolio_value": float(account.portfolio_value),
            }
        except Exception as e:
            raise BrokerError(f"Failed to get account: {e}")

    def get_positions(self) -> list[PositionInfo]:
        """Get current positions.

        Returns:
            List of dicts with keys: symbol, qty, avg_entry_price, current_price
        """
        try:
            positions = self.client.get_all_positions()
            return [
                {
                    "symbol": p.symbol,
                    "qty": float(p.qty),
                    "avg_entry_price": float(p.avg_entry_price) if p.avg_entry_price else None,
                    "current_price": float(p.current_price) if p.current_price else None,
                }
                for p in positions
            ]
        except Exception as e:
            raise BrokerError(f"Failed to get positions: {e}")

    def get_open_orders(self) -> list[OrderInfo]:
        """Get all open orders.

        Returns:
            List of dicts with keys: id, client_order_id, symbol, side, qty, status, filled_qty, etc
        """
        try:
            # Try with status parameter first (newer API), fall back if not supported
            try:
                orders = self.client.get_orders(status="open")
            except TypeError:
                # Older API version doesn't support status parameter
                orders = self.client.get_orders()
                # Filter to open orders manually
                orders = [
                    o
                    for o in orders
                    if o.status
                    and o.status.value not in ["filled", "canceled", "expired", "rejected"]
                ]

            return [
                {
                    "id": str(o.id),
                    "client_order_id": o.client_order_id,
                    "symbol": o.symbol,
                    "side": o.side.value if o.side else None,
                    "qty": float(o.qty) if o.qty else None,
                    "status": o.status.value if o.status else None,
                    "filled_qty": float(o.filled_qty) if o.filled_qty else 0,
                    "filled_avg_price": float(o.filled_avg_price) if o.filled_avg_price else None,
                    "created_at": o.created_at.isoformat() if o.created_at else None,
                }
                for o in orders
            ]
        except Exception as e:
            raise BrokerError(f"Failed to get open orders: {e}")

    def get_clock(self) -> ClockInfo:
        """Get market clock (FRESH CALL, NEVER CACHED).

        This is the authoritative source for whether trading is possible.
        MUST be called fresh immediately before each order submission.

        Returns:
            Dict with keys: is_open, next_open, next_close, timestamp
        """
        try:
            clock = self.client.get_clock()
            return {
                "is_open": clock.is_open,
                "next_open": clock.next_open.isoformat() if clock.next_open else None,
                "next_close": clock.next_close.isoformat() if clock.next_close else None,
                "timestamp": clock.timestamp.isoformat() if clock.timestamp else None,
            }
        except Exception as e:
            raise BrokerError(f"Failed to get clock: {e}")

    def submit_order(
        self,
        symbol: str,
        side: str,  # "buy" or "sell"
        qty: float,
        client_order_id: str,
        order_type: str = "market",  # "market" or "limit"
        limit_price: Optional[float] = None,
        time_in_force: str = "day",
    ) -> OrderInfo:
        """Submit a market or limit order.

        Args:
            symbol: Stock symbol
            side: "buy" or "sell"
            qty: Quantity
            client_order_id: Deterministic order ID
            order_type: "market" or "limit"
            limit_price: Required if order_type="limit"
            time_in_force: "day", "gtc", etc

        Returns:
            Dict with order details: id, client_order_id, symbol, status, etc
        """
        try:
            if order_type == "market":
                order_data = MarketOrderRequest(
                    symbol=symbol,
                    qty=qty,
                    side=OrderSide(side.lower()),
                    time_in_force=TimeInForce(time_in_force),
                    client_order_id=client_order_id,
                )
            elif order_type == "limit":
                if limit_price is None:
                    raise BrokerError("limit_price required for limit orders")
                # For now, use market orders only; limit support comes later
                order_data = MarketOrderRequest(
                    symbol=symbol,
                    qty=qty,
                    side=OrderSide(side.lower()),
                    time_in_force=TimeInForce(time_in_force),
                    client_order_id=client_order_id,
                )
            else:
                raise BrokerError(f"Invalid order_type: {order_type}")

            order = self.client.submit_order(order_data)
            return {
                "id": str(order.id),
                "client_order_id": order.client_order_id,
                "symbol": order.symbol,
                "side": order.side.value if order.side else None,
                "qty": float(order.qty) if order.qty else None,
                "status": order.status.value if order.status else None,
                "filled_qty": float(order.filled_qty) if order.filled_qty else 0,
                "filled_avg_price": (
                    float(order.filled_avg_price) if order.filled_avg_price else None
                ),
            }
        except Exception as e:
            raise BrokerError(f"Failed to submit order: {e}")

    def cancel_order(self, order_id: str) -> None:  # pragma: no cover
        """Cancel an open order.

        Args:
            order_id: Alpaca order ID
        """
        try:
            self.client.cancel_order(order_id)
        except Exception as e:
            raise BrokerError(f"Failed to cancel order {order_id}: {e}")
