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

from concurrent.futures import ThreadPoolExecutor
from concurrent.futures import TimeoutError as FutureTimeoutError
from dataclasses import dataclass
from typing import Any, Callable, Optional, TypeVar, Union

from alpaca.trading.client import TradingClient
from alpaca.trading.enums import OrderSide, TimeInForce
from alpaca.trading.models import Clock, Order, Position, TradeAccount
from alpaca.trading.requests import LimitOrderRequest, MarketOrderRequest

from src.utils import parse_optional_float

T = TypeVar("T")


@dataclass
class AccountInfo:
    equity: float = 0.0
    buying_power: float = 0.0
    cash: float = 0.0
    portfolio_value: float = 0.0

    def __getitem__(self, key: str) -> Any:
        return getattr(self, key)


from src.models.persistence import PositionInfo


@dataclass
class OrderInfo:
    id: str = ""
    client_order_id: str = ""
    symbol: str = ""
    side: Optional[str] = None
    qty: Optional[float] = None
    status: Optional[str] = None
    filled_qty: Optional[float] = None
    filled_avg_price: Optional[float] = None
    created_at: Optional[str] = None

    def __getitem__(self, key: str) -> Any:
        return getattr(self, key)


@dataclass
class ClockInfo:
    is_open: bool = False
    next_open: Optional[str] = None
    next_close: Optional[str] = None
    timestamp: Optional[str] = None

    def __getitem__(self, key: str) -> Any:
        return getattr(self, key)


class BrokerError(Exception):
    """Raised when broker operation fails."""

    pass


class Broker:
    """Thin synchronous broker wrapper around the Alpaca SDK.

    NOTE: This class is intentionally synchronous and MUST NOT create or
    manage its own threadpool. Execution policy, timeouts and retries are
    handled by the centralized `AsyncBrokerAdapter`.
    """

    def __init__(
        self,
        api_key: str,
        secret_key: str,
        paper: bool = True,
        query_timeout: float = 10.0,
        submit_timeout: float = 15.0,
    ) -> None:
        # Backwards-compatible timeouts expected by unit tests
        self.paper: bool = paper
        self.client: TradingClient = TradingClient(
            api_key=api_key,
            secret_key=secret_key,
            paper=paper,
        )
        self._default_timeout = query_timeout
        self._submit_timeout = submit_timeout
        self._timeouts = {
            "get_clock": query_timeout,
            "get_account": query_timeout,
            "get_positions": query_timeout,
            "get_open_orders": query_timeout,
            "submit_order": submit_timeout,
            "cancel_order": submit_timeout,
        }
        # Legacy executor used by older code/tests; kept for backwards compatibility.
        # Runtime paths should prefer `AsyncBrokerAdapter` which owns the real
        # concurrency boundary. The executor is test-only and is shut down via
        # `close`/`__del__` to avoid leaking threads.
        self._executor: Optional[ThreadPoolExecutor] = ThreadPoolExecutor(max_workers=1)

    def close(self) -> None:
        """Release any resources owned by this broker instance.

        In particular, this shuts down the legacy ThreadPoolExecutor used only by
        older tests so we don't leak threads in long-lived processes.
        """
        executor = getattr(self, "_executor", None)
        if executor is not None:
            try:
                executor.shutdown(wait=False)
            finally:
                self._executor = None

    def __del__(self) -> None:
        """Best-effort cleanup of resources on garbage collection."""
        try:
            self.close()
        except Exception:
            # Avoid raising from __del__; cleanup is best-effort only.
            pass

    def _call_with_timeout(
        self,
        func: Callable[[], T],
        timeout: Optional[float] = None,
        operation_name: Optional[str] = None,
    ) -> T:
        """Compatibility shim used by older tests.

        This implementation is intentionally synchronous and simple. The
        centralized `AsyncBrokerAdapter` provides the real concurrency,
        timeouts and retry behavior at runtime. Tests can patch this method
        to simulate timeouts or other behaviors.
        """
        # If an executor is present, use it so tests can inject fake executors
        # to simulate timeouts. Otherwise call synchronously.
        if hasattr(self, "_executor") and self._executor is not None:
            fut = self._executor.submit(func)
            try:
                return fut.result(timeout=timeout)
            except FutureTimeoutError:
                raise BrokerError(f"{operation_name or 'operation'} timed out")
            except Exception as e:
                op = f" ({operation_name})" if operation_name else ""
                raise BrokerError(f"Broker call failed{op}: {e}")

        # Fallback synchronous call
        try:
            return func()
        except Exception as e:
            op = f" ({operation_name})" if operation_name else ""
            raise BrokerError(f"Broker call failed{op}: {e}")

    def get_account(self) -> AccountInfo:
        """Get account info (equity, buying_power, etc).

        Returns:
            Dict with keys: equity, buying_power, cash, etc
        """
        try:
            account: Union[TradeAccount, dict[str, Any]] = self._call_with_timeout(
                lambda: self.client.get_account(),
                timeout=self._timeouts.get("get_account"),
                operation_name="get_account",
            )
            # Handle both SDK object and dict responses
            if isinstance(account, dict):
                return AccountInfo(
                    equity=float(account.get("equity", 0)),
                    buying_power=float(account.get("buying_power", 0)),
                    cash=float(account.get("cash", 0)),
                    portfolio_value=float(account.get("portfolio_value", 0)),
                )
            else:
                return AccountInfo(
                    equity=float(account.equity) if account.equity else 0.0,
                    buying_power=float(account.buying_power) if account.buying_power else 0.0,
                    cash=float(account.cash) if account.cash else 0.0,
                    portfolio_value=(
                        float(account.portfolio_value) if account.portfolio_value else 0.0
                    ),
                )
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
            positions_raw: Union[list[Position], dict[str, Any]] = self._call_with_timeout(
                lambda: self.client.get_all_positions(),
                timeout=self._timeouts.get("get_positions"),
                operation_name="get_positions",
            )
            positions: list[Any] = positions_raw if isinstance(positions_raw, list) else []
            result: list[PositionInfo] = []
            for p in positions:
                if isinstance(p, str):
                    continue  # Skip error strings
                result.append(
                    PositionInfo(
                        symbol=p.symbol,
                        qty=float(p.qty) if p.qty else 0.0,
                        avg_entry_price=(float(p.avg_entry_price) if p.avg_entry_price else None),
                        current_price=(float(p.current_price) if p.current_price else None),
                    )
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
            orders_raw: Union[list[Order], dict[str, Any]] = self._call_with_timeout(
                lambda: self.client.get_orders(),
                timeout=self._timeouts.get("get_open_orders"),
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
                    OrderInfo(
                        id=str(o.id) if o.id else "",
                        client_order_id=o.client_order_id if o.client_order_id else "",
                        symbol=o.symbol if o.symbol else "",
                        side=(o.side.value if o.side else None),
                        qty=(float(o.qty) if o.qty else None),
                        status=(o.status.value if o.status else None),
                        filled_qty=parse_optional_float(getattr(o, "filled_qty", None)),
                        filled_avg_price=(
                            parse_optional_float(getattr(o, "filled_avg_price", None))
                            if getattr(o, "filled_avg_price", None) is not None
                            else None
                        ),
                        created_at=(o.created_at.isoformat() if o.created_at else None),
                    )
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
            clock: Union[Clock, dict[str, Any]] = self._call_with_timeout(
                lambda: self.client.get_clock(),
                timeout=self._timeouts.get("get_clock"),
                operation_name="get_clock",
            )
            # Handle both SDK object and dict responses
            if isinstance(clock, dict):
                return ClockInfo(
                    is_open=bool(clock.get("is_open", False)),
                    next_open=clock.get("next_open"),
                    next_close=clock.get("next_close"),
                    timestamp=clock.get("timestamp"),
                )
            else:
                return ClockInfo(
                    is_open=clock.is_open if clock.is_open is not None else False,
                    next_open=(clock.next_open.isoformat() if clock.next_open else None),
                    next_close=(clock.next_close.isoformat() if clock.next_close else None),
                    timestamp=(clock.timestamp.isoformat() if clock.timestamp else None),
                )
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
                timeout=self._timeouts.get("submit_order"),
                operation_name="submit_order",
            )
            # Handle both SDK object and dict responses
            if isinstance(order_result, dict):
                return OrderInfo(
                    id=str(order_result.get("id", "")),
                    client_order_id=str(order_result.get("client_order_id", "")),
                    symbol=str(order_result.get("symbol", "")),
                    side=(str(order_result.get("side")) if order_result.get("side") else None),
                    qty=(float(order_result["qty"]) if order_result.get("qty") else None),
                    status=(str(order_result["status"]) if order_result.get("status") else None),
                    filled_qty=parse_optional_float(order_result.get("filled_qty")),
                    filled_avg_price=(
                        parse_optional_float(order_result.get("filled_avg_price"))
                        if order_result.get("filled_avg_price") is not None
                        else None
                    ),
                )
            else:
                return OrderInfo(
                    id=str(order_result.id) if order_result.id else "",
                    client_order_id=(
                        order_result.client_order_id if order_result.client_order_id else ""
                    ),
                    symbol=order_result.symbol if order_result.symbol else "",
                    side=(order_result.side.value if order_result.side else None),
                    qty=(float(order_result.qty) if order_result.qty else None),
                    status=(order_result.status.value if order_result.status else None),
                    filled_qty=parse_optional_float(getattr(order_result, "filled_qty", None)),
                    filled_avg_price=(
                        parse_optional_float(getattr(order_result, "filled_avg_price", None))
                        if getattr(order_result, "filled_avg_price", None) is not None
                        else None
                    ),
                )
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
                timeout=self._timeouts.get("cancel_order"),
                operation_name="cancel_order",
            )
        except BrokerError:
            raise
        except Exception as e:
            raise BrokerError(f"Failed to cancel order {order_id}: {e}")
