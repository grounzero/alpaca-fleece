"""Alpaca broker API wrapper with retry logic."""
import asyncio
import logging
from datetime import datetime, timedelta
from typing import Any, Dict, List, Optional
import pandas as pd
import pytz

from alpaca.trading.client import TradingClient
from alpaca.trading.requests import MarketOrderRequest, GetOrdersRequest
from alpaca.trading.enums import OrderSide, TimeInForce, QueryOrderStatus
from alpaca.data.historical import StockHistoricalDataClient
from alpaca.data.requests import StockBarsRequest
from alpaca.data.timeframe import TimeFrame

from src.config import Config


class BrokerError(Exception):
    """Base exception for broker errors."""
    pass


class Broker:
    """Wrapper for Alpaca Trading API with retry logic."""

    def __init__(self, config: Config, logger: logging.Logger):
        """
        Initialize broker.

        Args:
            config: Configuration object
            logger: Logger instance
        """
        self.config = config
        self.logger = logger

        # Initialize Alpaca clients
        # CRITICAL SAFETY: Use safety gate, not raw alpaca_paper flag
        # Live trading ONLY when BOTH ALPACA_PAPER=false AND ALLOW_LIVE_TRADING=true
        use_paper = not config.is_live_trading_enabled()
        self.trading_client = TradingClient(
            api_key=config.alpaca_api_key,
            secret_key=config.alpaca_secret_key,
            paper=use_paper,
        )

        self.data_client = StockHistoricalDataClient(
            api_key=config.alpaca_api_key,
            secret_key=config.alpaca_secret_key,
        )

        self.max_retries = 3
        self.retry_delay = 1.0

    async def _retry_operation(self, operation, *args, **kwargs) -> Any:
        """
        Retry operation with exponential backoff.

        Args:
            operation: Callable to retry
            *args: Positional arguments
            **kwargs: Keyword arguments

        Returns:
            Result of operation

        Raises:
            BrokerError: If all retries fail
        """
        last_error = None

        for attempt in range(self.max_retries):
            try:
                # Run operation (may be sync or async)
                if asyncio.iscoroutinefunction(operation):
                    return await operation(*args, **kwargs)
                else:
                    return operation(*args, **kwargs)

            except Exception as e:
                last_error = e
                error_msg = str(e).lower()

                # Don't retry on certain errors
                if any(x in error_msg for x in ["insufficient", "invalid", "forbidden", "unauthorized"]):
                    self.logger.error(f"Non-retryable error: {e}")
                    raise BrokerError(f"Broker operation failed: {e}") from e

                # Log and retry
                delay = self.retry_delay * (2 ** attempt)
                self.logger.warning(
                    f"Broker operation failed (attempt {attempt + 1}/{self.max_retries}), "
                    f"retrying in {delay}s: {e}"
                )

                if attempt < self.max_retries - 1:
                    await asyncio.sleep(delay)

        # All retries exhausted
        raise BrokerError(f"Broker operation failed after {self.max_retries} attempts: {last_error}") from last_error

    async def get_account(self) -> Dict[str, Any]:
        """Get account information."""
        def _get():
            account = self.trading_client.get_account()
            return {
                "equity": float(account.equity),
                "cash": float(account.cash),
                "buying_power": float(account.buying_power),
                "portfolio_value": float(account.portfolio_value),
                "pattern_day_trader": account.pattern_day_trader,
                "trading_blocked": account.trading_blocked,
                "account_blocked": account.account_blocked,
                "status": account.status,
            }

        return await self._retry_operation(_get)

    async def get_positions(self) -> List[Dict[str, Any]]:
        """Get all open positions."""
        def _get():
            positions = self.trading_client.get_all_positions()
            return [
                {
                    "symbol": pos.symbol,
                    "qty": float(pos.qty),
                    "side": "long" if float(pos.qty) > 0 else "short",
                    "market_value": float(pos.market_value),
                    "cost_basis": float(pos.cost_basis),
                    "unrealized_pl": float(pos.unrealized_pl),
                    "unrealized_plpc": float(pos.unrealized_plpc),
                    "current_price": float(pos.current_price),
                    "avg_entry_price": float(pos.avg_entry_price),
                }
                for pos in positions
            ]

        return await self._retry_operation(_get)

    async def get_open_orders(self) -> List[Dict[str, Any]]:
        """Get all open orders."""
        def _get():
            request = GetOrdersRequest(status=QueryOrderStatus.OPEN)
            orders = self.trading_client.get_orders(filter=request)
            return [
                {
                    "id": order.id,
                    "client_order_id": order.client_order_id,
                    "symbol": order.symbol,
                    "side": order.side.value,
                    "qty": float(order.qty) if order.qty else None,
                    "filled_qty": float(order.filled_qty) if order.filled_qty else 0,
                    "order_type": order.order_type.value,
                    "status": order.status.value,
                    "created_at": order.created_at,
                }
                for order in orders
            ]

        return await self._retry_operation(_get)

    async def submit_order(
        self,
        symbol: str,
        side: str,
        qty: float,
        client_order_id: str,
        order_type: str = "market",
        time_in_force: str = "day",
    ) -> Dict[str, Any]:
        """
        Submit an order.

        Args:
            symbol: Stock symbol
            side: "buy" or "sell"
            qty: Quantity
            client_order_id: Unique client order ID
            order_type: Order type (default: market)
            time_in_force: Time in force (default: day)

        Returns:
            Order details
        """
        def _submit():
            order_side = OrderSide.BUY if side.lower() == "buy" else OrderSide.SELL

            request = MarketOrderRequest(
                symbol=symbol,
                qty=qty,
                side=order_side,
                time_in_force=TimeInForce.DAY if time_in_force == "day" else TimeInForce.GTC,
                client_order_id=client_order_id,
            )

            order = self.trading_client.submit_order(request)

            return {
                "id": order.id,
                "client_order_id": order.client_order_id,
                "symbol": order.symbol,
                "side": order.side.value,
                "qty": float(order.qty) if order.qty else None,
                "filled_qty": float(order.filled_qty) if order.filled_qty else 0,
                "status": order.status.value,
                "created_at": order.created_at,
            }

        return await self._retry_operation(_submit)

    async def cancel_order(self, order_id: str):
        """Cancel an order by ID."""
        def _cancel():
            self.trading_client.cancel_order_by_id(order_id)

        await self._retry_operation(_cancel)

    async def get_bars(
        self,
        symbol: str,
        timeframe: str,
        start: datetime,
        end: Optional[datetime] = None,
        limit: int = 1000,
    ) -> pd.DataFrame:
        """
        Get historical bars.

        Args:
            symbol: Stock symbol
            timeframe: Bar timeframe (e.g., "1Min", "1Hour")
            start: Start time (timezone-aware)
            end: End time (timezone-aware), defaults to now
            limit: Maximum number of bars

        Returns:
            DataFrame with OHLCV data
        """
        def _get():
            # Parse timeframe
            timeframe_map = {
                "1Min": TimeFrame.Minute,
                "5Min": TimeFrame(5, "Min"),
                "15Min": TimeFrame(15, "Min"),
                "1Hour": TimeFrame.Hour,
                "1Day": TimeFrame.Day,
            }

            tf = timeframe_map.get(timeframe, TimeFrame.Minute)

            # Ensure timezone-aware datetimes
            if start.tzinfo is None:
                start_tz = pytz.timezone("America/New_York").localize(start)
            else:
                start_tz = start

            if end is None:
                end_tz = datetime.now(pytz.timezone("America/New_York"))
            elif end.tzinfo is None:
                end_tz = pytz.timezone("America/New_York").localize(end)
            else:
                end_tz = end

            request = StockBarsRequest(
                symbol_or_symbols=symbol,
                timeframe=tf,
                start=start_tz,
                end=end_tz,
                limit=limit,
            )

            bars = self.data_client.get_stock_bars(request)

            if symbol not in bars.data or len(bars.data[symbol]) == 0:
                return pd.DataFrame()

            # Convert to DataFrame
            data = []
            for bar in bars.data[symbol]:
                data.append({
                    "timestamp": bar.timestamp,
                    "open": float(bar.open),
                    "high": float(bar.high),
                    "low": float(bar.low),
                    "close": float(bar.close),
                    "volume": int(bar.volume),
                    "vwap": float(bar.vwap) if bar.vwap else None,
                })

            df = pd.DataFrame(data)
            df.set_index("timestamp", inplace=True)
            df.sort_index(inplace=True)

            return df

        return await self._retry_operation(_get)
