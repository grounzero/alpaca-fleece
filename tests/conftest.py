"""Shared test fixtures."""

import tempfile
from datetime import datetime, timezone
from unittest.mock import MagicMock

import pytest

from src.broker import Broker
from src.event_bus import EventBus
from src.schema_manager import SchemaManager
from src.state_store import StateStore


@pytest.fixture
def tmp_db():
    """Temporary SQLite database for tests."""
    with tempfile.NamedTemporaryFile(suffix=".db", delete=False) as f:
        db_path = f.name

    yield db_path

    import os

    try:
        os.unlink(db_path)
    except OSError:
        pass


@pytest.fixture
def state_store(tmp_db):
    """State store with temporary database (schema ensured by SchemaManager)."""
    SchemaManager.ensure_schema(tmp_db)
    return StateStore(tmp_db)


@pytest.fixture
def event_bus():
    """Event bus for testing."""
    bus = EventBus()
    bus.running = True  # Start in running state
    return bus


@pytest.fixture
def mock_broker():
    """Mock broker client. Returns an async-compatible shim wrapping a sync MagicMock.

    Runtime modules expect an async broker interface; tests historically used a
    sync `Broker` MagicMock. Provide a lightweight shim with `async def` methods
    delegating to the sync mock to maintain existing test semantics while
    satisfying the async adapter contract.
    """
    broker_sync = MagicMock(spec=Broker)

    # Mock account
    broker_sync.get_account.return_value = {
        "equity": 10000.0,
        "buying_power": 5000.0,
        "cash": 2000.0,
        "portfolio_value": 12000.0,
    }

    # Mock positions
    broker_sync.get_positions.return_value = []

    # Mock open orders
    broker_sync.get_open_orders.return_value = []

    # Mock clock (market open)
    broker_sync.get_clock.return_value = {
        "is_open": True,
        "next_open": None,
        "next_close": None,
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }

    class AsyncBrokerShim:
        def __init__(self, sync):
            self._sync = sync
            # Expose awaitable wrappers that proxy to the underlying MagicMock methods
            self.get_account = self._AwaitableMockMethod(self._sync.get_account)
            self.get_positions = self._AwaitableMockMethod(self._sync.get_positions)
            self.get_open_orders = self._AwaitableMockMethod(self._sync.get_open_orders)
            self.get_clock = self._AwaitableMockMethod(self._sync.get_clock)
            self.submit_order = self._AwaitableMockMethod(self._sync.submit_order)
            self.cancel_order = self._AwaitableMockMethod(self._sync.cancel_order)
            # Provide no-op awaitable methods for adapter lifecycle and cache
            # to satisfy the AsyncBrokerInterface used in runtime.
            async def _noop_invalidate(*keys: str) -> None:  # type: ignore[unused-def]
                return None

            async def _noop_close() -> None:  # type: ignore[unused-def]
                return None

            self.invalidate_cache = _noop_invalidate
            self.close = _noop_close

        class _AwaitableMockMethod:
            def __init__(self, sync_method):
                self._sync = sync_method

            @property
            def return_value(self):
                return self._sync.return_value

            @return_value.setter
            def return_value(self, v):
                self._sync.return_value = v

            @property
            def side_effect(self):
                return self._sync.side_effect

            @side_effect.setter
            def side_effect(self, v):
                self._sync.side_effect = v

            async def __call__(self, *args, **kwargs):
                return self._sync(*args, **kwargs)

    return AsyncBrokerShim(broker_sync)


@pytest.fixture
def mock_market_data_client():
    """Mock market data client."""
    client = MagicMock()

    # Mock bars
    import pandas as pd

    client.get_bars.return_value = pd.DataFrame(
        {
            "open": [100.0, 101.0, 102.0],
            "high": [101.0, 102.0, 103.0],
            "low": [99.0, 100.0, 101.0],
            "close": [100.5, 101.5, 102.5],
            "volume": [1000, 1100, 1200],
            "trade_count": [10, 11, 12],
            "vwap": [100.2, 101.2, 102.2],
        },
        index=pd.DatetimeIndex(
            [
                datetime(2024, 1, 1, 10, 0, tzinfo=timezone.utc),
                datetime(2024, 1, 1, 10, 1, tzinfo=timezone.utc),
                datetime(2024, 1, 1, 10, 2, tzinfo=timezone.utc),
            ]
        ),
    )

    # Mock snapshot
    client.get_snapshot.return_value = {
        "bid": 100.0,
        "ask": 100.1,
        "bid_size": 100,
        "ask_size": 100,
        "last_quote_time": datetime.now(timezone.utc).isoformat(),
    }

    return client


@pytest.fixture
def config():
    """Base trading config for tests."""
    return {
        "symbols": {
            "mode": "explicit",
            "list": ["AAPL", "MSFT"],
        },
        "trading": {
            "session_policy": "regular_only",
            "bar_timeframe": "1Min",
            "stream_feed": "iex",
            "shutdown_at_close": False,
        },
        "filters": {
            "max_spread_pct": 0.005,
            "min_bar_trades": 10,
        },
        "execution": {
            "order_type": "market",
            "time_in_force": "day",
        },
        "strategy": {
            "name": "sma_crossover",
            "params": {
                "fast_period": 10,
                "slow_period": 30,
            },
        },
        "risk": {
            "max_position_pct": 0.10,
            "max_daily_loss_pct": 0.05,
            "max_trades_per_day": 20,
            "max_concurrent_positions": 10,
        },
        "shutdown": {
            "cancel_open_orders": False,
        },
    }
