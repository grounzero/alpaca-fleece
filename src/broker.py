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

import logging
import time
from concurrent.futures import ThreadPoolExecutor
from concurrent.futures import TimeoutError as FutureTimeoutError
from typing import Any, Callable, Optional, TypedDict, TypeVar, Union

from alpaca.trading.client import TradingClient

logger = logging.getLogger(__name__)
from alpaca.trading.enums import OrderSide, TimeInForce
from alpaca.trading.models import Clock, Order, Position, TradeAccount
from alpaca.trading.requests import LimitOrderRequest, MarketOrderRequest

T = TypeVar("T")


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
    """Execution-only broker client with timeout protection."""

    def __init__(
        self,
        api_key: str,
        secret_key: str,
        paper: bool = True,
        query_timeout: float = 10.0,
        submit_timeout: float = 15.0,
    ) -> None:
        """Initialise broker client.

        Args:
            api_key: Alpaca API key
            secret_key: Alpaca secret key
            paper: True for paper trading, False for live
            query_timeout: Timeout in seconds for query operations (account, positions, orders, clock)
            submit_timeout: Timeout in seconds for order submission/cancellation
        """
        self.paper: bool = paper
        self._default_timeout: float = query_timeout
        self._submit_timeout: float = submit_timeout
        self._executor: ThreadPoolExecutor = ThreadPoolExecutor(max_workers=2)
        self.client: TradingClient = TradingClient(
            api_key=api_key,
            secret_key=secret_key,
            paper=paper,
        )

    def _call_with_timeout(
        self,
        func: Callable[[], T],
        timeout: Optional[float] = None,
        operation_name: str = "broker call",
    ) -> T:
        """Execute a function with timeout protection.

        Args:
            func: The function to execute
            timeout: Timeout in seconds (uses default if None)
            operation_name: Name of operation for error messages

        Returns:
            Result of func()

        Raises:
            BrokerError: If the call times out or fails
        """
        timeout = timeout or self._default_timeout
        future = self._executor.submit(func)
        try:
            return future.result(timeout=timeout)
        except FutureTimeoutError:
            raise BrokerError(f"Broker call '{operation_name}' timed out after {timeout}s")
        except BrokerError:
            raise
        except Exception as e:
            raise BrokerError(f"Broker call '{operation_name}' failed: {e}")

    def _retry_with_backoff(
        self,
        func: Callable[[], T],
        max_retries: int = 3,
        base_delay: float = 1.0,
        operation_name: str = "broker call",
    ) -> T:
        """Retry function with exponential backoff.

        Only use for idempotent GET operations (get_account, get_positions, get_orders, get_clock).
        Do NOT use for submit_order or cancel_order.

        Args:
            func: The function to execute
            max_retries: Maximum number of retry attempts
            base_delay: Initial delay in seconds between retries
            operation_name: Name of operation for error messages

        Returns:
            Result of func()

        Raises:
            BrokerError: If all retries fail
        """
        last_error: Optional[BrokerError] = None
        for attempt in range(max_retries):
            try:
                return self._call_with_timeout(func, operation_name=operation_name)
            except BrokerError as e:
                last_error = e
                if attempt < max_retries - 1:
                    delay = base_delay * (2**attempt)
                    logger.warning(
                        f"{operation_name} failed (attempt {attempt + 1}/{max_retries}), "
                        f"retrying in {delay}s: {e}"
                    )
                    time.sleep(delay)
        raise BrokerError(f"{operation_name} failed after {max_retries} attempts: {last_error}")

    def get_account(self) -> AccountInfo:
        """Get account info (equity, buying_power, etc).

        Returns:
            Dict with keys: equity, buying_power, cash, etc
        """
        try:
            account: Union[TradeAccount, dict[str, Any]] = self._retry_with_backoff(
                self.client.get_account,
                operation_name="get_account",
            )
            # Handle both SDK object and dict responses
            if isinstance(account, dict):
                return {
                    "equity": float(account.get("equity", 0)),
                    "buying_power": float(account.get("buying_power", 0)),
                    "cash": float(account.get("cash", 0)),
                    "portfolio_value": float(account.get("portfolio_value", 0)),
                }
            else:
                return {
                    "equity": float(account.equity) if account.equity else 0.0,
                    "buying_power": float(account.buying_power) if account.buying_power else 0.0,
                    "cash": float(account.cash) if account.cash else 0.0,
                    "portfolio_value": (
                        float(account.portfolio_value) if account.portfolio_value else 0.0
                    ),
                }
        except BrokerError:
            raise
        except Exception as e:
            raise BrokerError(f"Failed to get account: {e}")

    def get_positions(self) -> list[PositionInfo]:
        """Get current positions.

        Returns:
            List of dicts with keys: symbol, qty, avg_entry_price, current_price
        """
        try:
            positions_raw: Union[list[Position], dict[str, Any]] = self._retry_with_backoff(
                self.client.get_all_positions,
                operation_name="get_positions",
            )
            positions: list[Any] = positions_raw if isinstance(positions_raw, list) else []
            result: list[PositionInfo] = []
            for p in positions:
                if isinstance(p, str):
                    continue  # Skip error strings
                result.append(
                    {
                        "symbol": p.symbol,
                        "qty": float(p.qty) if p.qty else 0.0,
                        "avg_entry_price": float(p.avg_entry_price) if p.avg_entry_price else None,
                        "current_price": float(p.current_price) if p.current_price else None,
                    }
                )
            return result
        except BrokerError:
            raise
        except Exception as e:
            raise BrokerError(f"Failed to get positions: {e}")

    def get_open_orders(self) -> list[OrderInfo]:
        """Get all open orders.

        Returns:
            List of dicts with keys: id, client_order_id, symbol, side, qty, status, filled_qty, etc
        """
        try:
            # Get orders and filter to open ones manually
            orders_raw: Union[list[Order], dict[str, Any]] = self._retry_with_backoff(
                self.client.get_orders,
                operation_name="get_open_orders",
            )
            orders: list[Any] = orders_raw if isinstance(orders_raw, list) else []
            open_orders: list[Union[Order, str]] = [
                o
                for o in orders
                if isinstance(o, Order)
                and o.status
                and o.status.value not in ["filled", "canceled", "expired", "rejected"]
            ]

            result: list[OrderInfo] = []
            for o in open_orders:
                if isinstance(o, str):
                    continue  # Skip error strings
                result.append(
                    {
                        "id": str(o.id) if o.id else "",
                        "client_order_id": o.client_order_id if o.client_order_id else "",
                        "symbol": o.symbol if o.symbol else "",
                        "side": o.side.value if o.side else None,
                        "qty": float(o.qty) if o.qty else None,
                        "status": o.status.value if o.status else None,
                        "filled_qty": float(o.filled_qty) if o.filled_qty else 0,
                        "filled_avg_price": (
                            float(o.filled_avg_price) if o.filled_avg_price else None
                        ),
                        "created_at": o.created_at.isoformat() if o.created_at else None,
                    }
                )
            return result
        except BrokerError:
            raise
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
            clock: Union[Clock, dict[str, Any]] = self._retry_with_backoff(
                self.client.get_clock,
                operation_name="get_clock",
            )
            # Handle both SDK object and dict responses
            if isinstance(clock, dict):
                return {
                    "is_open": bool(clock.get("is_open", False)),
                    "next_open": clock.get("next_open"),
                    "next_close": clock.get("next_close"),
                    "timestamp": clock.get("timestamp"),
                }
            else:
                return {
                    "is_open": clock.is_open if clock.is_open is not None else False,
                    "next_open": clock.next_open.isoformat() if clock.next_open else None,
                    "next_close": clock.next_close.isoformat() if clock.next_close else None,
                    "timestamp": clock.timestamp.isoformat() if clock.timestamp else None,
                }
        except BrokerError:
            raise
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
            order_data: Union[MarketOrderRequest, LimitOrderRequest]
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
                order_data = LimitOrderRequest(
                    symbol=symbol,
                    qty=qty,
                    side=OrderSide(side.lower()),
                    limit_price=limit_price,
                    time_in_force=TimeInForce(time_in_force),
                    client_order_id=client_order_id,
                )
            else:
                raise BrokerError(f"Invalid order_type: {order_type}")

            order_result: Union[Order, dict[str, Any]] = self._call_with_timeout(
                lambda: self.client.submit_order(order_data),
                timeout=self._submit_timeout,
                operation_name="submit_order",
            )
            # Handle both SDK object and dict responses
            if isinstance(order_result, dict):
                return {
                    "id": str(order_result.get("id", "")),
                    "client_order_id": str(order_result.get("client_order_id", "")),
                    "symbol": str(order_result.get("symbol", "")),
                    "side": str(order_result.get("side")) if order_result.get("side") else None,
                    "qty": float(order_result["qty"]) if order_result.get("qty") else None,
                    "status": str(order_result["status"]) if order_result.get("status") else None,
                    "filled_qty": (
                        float(order_result["filled_qty"]) if order_result.get("filled_qty") else 0
                    ),
                    "filled_avg_price": (
                        float(order_result["filled_avg_price"])
                        if order_result.get("filled_avg_price")
                        else None
                    ),
                }
            else:
                return {
                    "id": str(order_result.id) if order_result.id else "",
                    "client_order_id": (
                        order_result.client_order_id if order_result.client_order_id else ""
                    ),
                    "symbol": order_result.symbol if order_result.symbol else "",
                    "side": order_result.side.value if order_result.side else None,
                    "qty": float(order_result.qty) if order_result.qty else None,
                    "status": order_result.status.value if order_result.status else None,
                    "filled_qty": float(order_result.filled_qty) if order_result.filled_qty else 0,
                    "filled_avg_price": (
                        float(order_result.filled_avg_price)
                        if order_result.filled_avg_price
                        else None
                    ),
                }
        except BrokerError:
            raise
        except Exception as e:
            raise BrokerError(f"Failed to submit order: {e}")

    def cancel_order(self, order_id: str) -> None:  # pragma: no cover
        """Cancel an open order.

        Args:
            order_id: Alpaca order ID
        """
        try:
            self._call_with_timeout(
                lambda: self.client.cancel_order_by_id(order_id),
                timeout=self._submit_timeout,
                operation_name="cancel_order",
            )
        except BrokerError:
            raise
        except Exception as e:
            raise BrokerError(f"Failed to cancel order {order_id}: {e}")

    def close(self) -> None:
        """Clean up resources (executor, connections)."""
        self._executor.shutdown(wait=False)
