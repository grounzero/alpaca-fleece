"""Async Broker Adapter

Centralised concurrency boundary for synchronous `Broker` calls.

Responsibilities:
- Single bounded ThreadPoolExecutor
- Per-method timeouts
- Retry policy for read-only calls
- TTL caching for hot reads (clock, account, positions)
- Metrics counters and structured logging
- Exception taxonomy mapping
"""

from __future__ import annotations

import asyncio
import logging
import random
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from typing import Any, Callable, Dict, List, Optional, Protocol, cast

from src.broker import Broker

logger = logging.getLogger(__name__)


class BrokerTimeoutError(Exception):
    pass


class BrokerTransientError(Exception):
    pass


class BrokerFatalError(Exception):
    pass


class AsyncBrokerInterface(Protocol):
    async def get_clock(self) -> Dict[str, Any]: ...

    async def get_account(self) -> Dict[str, Any]: ...

    async def get_positions(self) -> List[Dict[str, Any]]: ...

    async def get_open_orders(self) -> List[Dict[str, Any]]: ...

    async def submit_order(self, *args: Any, **kwargs: Any) -> Dict[str, Any]: ...

    async def cancel_order(self, order_id: str) -> None: ...


@dataclass
class _CacheItem:
    value: Any
    expires_at: float


class AsyncBrokerAdapter(AsyncBrokerInterface):
    def __init__(
        self,
        broker: Broker,
        max_workers: int = 8,
        enable_cache: bool = True,
        clock_ttl: float = 2.0,
        account_ttl: float = 1.0,
        positions_ttl: float = 1.0,
    ) -> None:
        self._broker = broker
        self._executor = ThreadPoolExecutor(max_workers=max_workers)
        self._enable_cache = enable_cache
        self._cache: Dict[str, _CacheItem] = {}

        # TTLs (seconds)
        self._clock_ttl = clock_ttl
        self._account_ttl = account_ttl
        self._positions_ttl = positions_ttl

        # Timeouts per method (seconds)
        self._timeouts = {
            "get_clock": 5.0,
            "get_account": 5.0,
            "get_positions": 5.0,
            "get_open_orders": 5.0,
            "submit_order": 10.0,
            "cancel_order": 10.0,
        }

        # Retry policy for read-only calls
        self._max_retries = 3
        self._base_backoff = 0.1

        # Metrics simple counters
        self.metrics: Dict[str, int] = {}

    # --- Internal helpers -------------------------------------------------
    def _metric_inc(self, key: str) -> None:
        self.metrics[key] = self.metrics.get(key, 0) + 1

    def _cache_get(self, key: str) -> Optional[Any]:
        if not self._enable_cache:
            return None
        it = self._cache.get(key)
        if not it:
            self._metric_inc(f"broker_cache_misses_total{{method={key}}}")
            return None
        if time.time() >= it.expires_at:
            del self._cache[key]
            self._metric_inc(f"broker_cache_misses_total{{method={key}}}")
            return None
        self._metric_inc(f"broker_cache_hits_total{{method={key}}}")
        return it.value

    def _cache_set(self, key: str, value: Any, ttl: float) -> None:
        if not self._enable_cache:
            return
        self._cache[key] = _CacheItem(value=value, expires_at=time.time() + ttl)

    async def _run_sync(
        self,
        func: Callable[[], Any],
        method_name: str,
        timeout: Optional[float] = None,
        retry: bool = False,
    ) -> Any:
        timeout = timeout or self._timeouts.get(method_name)

        attempt = 0
        last_exc: Optional[Exception] = None
        while True:
            attempt += 1
            start = time.time()
            try:
                loop = asyncio.get_running_loop()
                fut = loop.run_in_executor(self._executor, func)
                self._metric_inc(f"broker_calls_total{{method={method_name}}}")
                result = await asyncio.wait_for(fut, timeout=timeout)
                dur = int((time.time() - start) * 1000)
                logger.info(
                    "broker_call method=%s duration_ms=%d outcome=success",
                    method_name,
                    dur,
                )
                self._metric_inc(f"broker_latency_ms{{method={method_name}}}")
                return result
            except asyncio.TimeoutError:
                last_exc = BrokerTimeoutError(f"{method_name} timed out after {timeout}s")
                self._metric_inc(f"broker_timeouts_total{{method={method_name}}}")
                logger.warning(
                    "broker_call method=%s duration_ms=%d outcome=timeout",
                    method_name,
                    int((time.time() - start) * 1000),
                )
                # Timeouts are handled as transient for reads only
                if not retry or attempt >= self._max_retries:
                    raise last_exc
            except Exception as e:
                # Map underlying exceptions to taxonomy
                last_exc = BrokerTransientError(str(e))
                self._metric_inc(f"broker_retries_total{{method={method_name}}}")
                logger.warning(
                    "broker_call method=%s duration_ms=%d outcome=transient exception=%s",
                    method_name,
                    int((time.time() - start) * 1000),
                    e,
                )
                # Retry if configured
                if not retry or attempt >= self._max_retries:
                    raise last_exc

            # Backoff before retrying
            backoff = self._base_backoff * (2 ** (attempt - 1))
            # jitter
            backoff = backoff * (0.5 + random.random() * 0.5)
            await asyncio.sleep(backoff)

    # --- Public API -------------------------------------------------------
    async def get_clock(self) -> Dict[str, Any]:
        key = "get_clock"
        # Cache short TTL
        cached = self._cache_get(key)
        if cached is not None:
            return cast(Dict[str, Any], cached)

        def func() -> Any:
            return self._broker.get_clock()

        res = cast(
            Dict[str, Any],
            await self._run_sync(
                func, method_name=key, timeout=self._timeouts["get_clock"], retry=True
            ),
        )
        self._cache_set(key, res, self._clock_ttl)
        return res

    async def get_account(self) -> Dict[str, Any]:
        key = "get_account"
        cached = self._cache_get(key)
        if cached is not None:
            return cast(Dict[str, Any], cached)

        def func() -> Any:
            return self._broker.get_account()

        res = cast(
            Dict[str, Any],
            await self._run_sync(
                func, method_name=key, timeout=self._timeouts["get_account"], retry=True
            ),
        )
        self._cache_set(key, res, self._account_ttl)
        return res

    async def get_positions(self) -> List[Dict[str, Any]]:
        key = "get_positions"
        cached = self._cache_get(key)
        if cached is not None:
            return cast(List[Dict[str, Any]], cached)

        def func() -> Any:
            return self._broker.get_positions()

        res = cast(
            List[Dict[str, Any]],
            await self._run_sync(
                func, method_name=key, timeout=self._timeouts["get_positions"], retry=True
            ),
        )
        self._cache_set(key, res, self._positions_ttl)
        return res

    async def get_open_orders(self) -> List[Dict[str, Any]]:
        def func() -> Any:
            return self._broker.get_open_orders()

        return cast(
            List[Dict[str, Any]],
            await self._run_sync(
                func,
                method_name="get_open_orders",
                timeout=self._timeouts["get_open_orders"],
                retry=True,
            ),
        )

    async def submit_order(self, *args: Any, **kwargs: Any) -> Dict[str, Any]:
        def func() -> Any:
            return self._broker.submit_order(*args, **kwargs)

        # No retries for writes
        return cast(
            Dict[str, Any],
            await self._run_sync(
                func,
                method_name="submit_order",
                timeout=self._timeouts["submit_order"],
                retry=False,
            ),
        )

    async def cancel_order(self, order_id: str) -> None:
        def func() -> Any:
            return self._broker.cancel_order(order_id)

        await self._run_sync(
            func, method_name="cancel_order", timeout=self._timeouts["cancel_order"], retry=False
        )
