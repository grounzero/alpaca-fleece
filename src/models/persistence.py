from dataclasses import dataclass
from datetime import datetime
from typing import Optional, Any


@dataclass
class OrderIntent:
    client_order_id: str
    strategy: Optional[str]
    symbol: str
    side: str
    qty: float
    atr: Optional[float]
    status: str
    filled_qty: Optional[float]
    filled_avg_price: Optional[float]
    alpaca_order_id: Optional[str]

    def to_dict(self) -> dict[str, Any]:
        return {
            "client_order_id": self.client_order_id,
            "strategy": self.strategy,
            "symbol": self.symbol,
            "side": self.side,
            "qty": float(self.qty),
            "atr": self.atr,
            "status": self.status,
            "filled_qty": self.filled_qty,
            "filled_avg_price": self.filled_avg_price,
            "alpaca_order_id": self.alpaca_order_id,
        }


@dataclass
class Fill:
    alpaca_order_id: str
    client_order_id: str
    symbol: str
    side: str
    delta_qty: float
    cum_qty: float
    cum_avg_price: Optional[float]
    timestamp_utc: Optional[datetime]
    fill_id: Optional[str]
    price_is_estimate: bool = True
    delta_fill_price: Optional[float] = None


@dataclass
class Position:
    symbol: str
    side: str
    qty: float
    entry_price: float
    entry_time: Optional[datetime]
    extreme_price: float
    atr: Optional[float] = None
    trailing_stop_price: Optional[float] = None
    trailing_stop_activated: bool = False
    pending_exit: bool = False
    updated_at: Optional[datetime] = None

    def to_dict(self) -> dict[str, Any]:
        return {
            "symbol": self.symbol,
            "side": self.side,
            "qty": float(self.qty),
            "entry_price": float(self.entry_price),
            "entry_time": self.entry_time.isoformat() if self.entry_time else None,
            "extreme_price": float(self.extreme_price),
            "atr": self.atr,
            "trailing_stop_price": self.trailing_stop_price,
            "trailing_stop_activated": int(self.trailing_stop_activated),
            "pending_exit": int(self.pending_exit),
            "updated_at": self.updated_at.isoformat() if self.updated_at else None,
        }


@dataclass
class ExitAttempt:
    symbol: str
    attempt_count: int
    last_attempt_ts_utc: Optional[datetime]
    reason: Optional[str] = None
