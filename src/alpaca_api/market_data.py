"""Market data endpoints: /v2/stocks/bars, /v2/stocks/snapshots."""

from datetime import datetime
from typing import Any, Optional

import pandas as pd
from alpaca.data.requests import StockBarsRequest, StockLatestQuoteRequest
from alpaca.data.timeframe import TimeFrame, TimeFrameUnit

from .base import AlpacaDataClient, AlpacaDataClientError


class MarketDataClient(AlpacaDataClient):
    """Fetch market data: bars, snapshots."""

    def get_bars(
        self,
        symbol: str,
        timeframe: str = "1Min",
        start: Optional[datetime] = None,
        end: Optional[datetime] = None,
        limit: Optional[int] = None,
    ) -> pd.DataFrame:
        """Fetch historical bars.

        Args:
            symbol: Stock symbol
            timeframe: "1Min", "5Min", "15Min", "1H", "1D", etc
            start: Start time (UTC)
            end: End time (UTC)
            limit: Max bars to return

        Returns:
            DataFrame with columns: open, high, low, close, volume, trade_count, vwap, timestamp
        """
        try:
            tf_map = {
                "1Min": TimeFrame(1, TimeFrameUnit.Minute),
                "5Min": TimeFrame(5, TimeFrameUnit.Minute),
                "15Min": TimeFrame(15, TimeFrameUnit.Minute),
                "1H": TimeFrame(1, TimeFrameUnit.Hour),
                "1D": TimeFrame(1, TimeFrameUnit.Day),
            }
            tf = tf_map.get(timeframe, TimeFrame(1, TimeFrameUnit.Minute))

            request = StockBarsRequest(
                symbol_or_symbols=symbol,
                timeframe=tf,
                start=start,
                end=end,
                limit=limit,
            )
            bars = self.client.get_stock_bars(request)

            if symbol not in bars:
                return pd.DataFrame()

            df = bars[symbol].df
            # Convert column names
            df = df.rename(
                columns={
                    "n": "trade_count",
                }
            )
            return df
        except Exception as e:
            raise AlpacaDataClientError(f"Failed to get bars for {symbol}: {e}")

    def get_snapshot(self, symbol: str) -> dict[str, Any]:
        """Fetch latest snapshot (bid/ask for spread calculation).

        Args:
            symbol: Stock symbol

        Returns:
            Dict with keys: bid, ask, bid_size, ask_size, last_quote_time
        """
        try:
            request = StockLatestQuoteRequest(symbol_or_symbols=symbol)
            quote = self.client.get_stock_latest_quote(request)

            if symbol not in quote:
                return {}

            q = quote[symbol]
            # Quote objects have bid_price, ask_price attributes (not last_quote_time)
            return {
                "bid": float(q.bid_price) if q.bid_price else None,
                "ask": float(q.ask_price) if q.ask_price else None,
                "bid_size": float(q.bid_size) if q.bid_size else None,
                "ask_size": float(q.ask_size) if q.ask_size else None,
                "last_quote_time": (
                    q.timestamp.isoformat() if hasattr(q, "timestamp") and q.timestamp else None
                ),
            }
        except Exception as e:
            raise AlpacaDataClientError(f"Failed to get snapshot for {symbol}: {e}")
