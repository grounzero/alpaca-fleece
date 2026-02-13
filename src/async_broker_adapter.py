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
from src.broker import BrokerError as SyncBrokerError

# Use asyncio.Lock for async-safe cache protection


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
    async def invalidate_cache(self, *keys: str) -> None: ...
    async def close(self) -> None: ...


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
        # Protect cache and metrics for safe concurrent access from
        # multiple coroutines running on the event loop.
        self._cache_lock = asyncio.Lock()
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
    async def _metric_inc(self, key: str) -> None:
        # Protect metrics map updates
        async with self._cache_lock:
            self.metrics[key] = self.metrics.get(key, 0) + 1

    async def _cache_get(self, key: str) -> Optional[Any]:
        if not self._enable_cache:
            return None
        async with self._cache_lock:
            it = self._cache.get(key)
            if not it:
                # record miss
                self.metrics[f"broker_cache_misses_total{{method={key}}}"] = (
                    self.metrics.get(f"broker_cache_misses_total{{method={key}}}", 0) + 1
                )
                return None

            if time.time() >= it.expires_at:
                del self._cache[key]
                self.metrics[f"broker_cache_misses_total{{method={key}}}"] = (
                    self.metrics.get(f"broker_cache_misses_total{{method={key}}}", 0) + 1
                )
                return None

            # hit
            self.metrics[f"broker_cache_hits_total{{method={key}}}"] = (
                self.metrics.get(f"broker_cache_hits_total{{method={key}}}", 0) + 1
            )
            return it.value

    async def _cache_set(self, key: str, value: Any, ttl: float) -> None:
        if not self._enable_cache:
            return
        async with self._cache_lock:
            self._cache[key] = _CacheItem(value=value, expires_at=time.time() + ttl)

    async def invalidate_cache(self, *keys: str) -> None:
        """Invalidate specific cache keys.

        Usage: `await adapter.invalidate_cache("get_positions", "get_open_orders")`.
        """
        if not self._enable_cache:
            return
        for k in keys:
            if k in self._cache:
                del self._cache[k]
                await self._metric_inc(f"broker_cache_invalidations_total{{method={k}}}")

    async def close(self) -> None:
        """Shutdown the adapter's threadpool and release resources.

        This is safe to call multiple times. Prefer `async with AsyncBrokerAdapter(...)`
        or explicitly `await adapter.close()` during shutdown.
        """
        # If executor already shutdown or disabled, nothing to do
        exec_ref = getattr(self, "_executor", None)
        if exec_ref is None:
            return

        try:
            # Shutdown is blocking; run it off the event loop to avoid blocking
            loop = asyncio.get_running_loop()
            await loop.run_in_executor(None, exec_ref.shutdown, True)
        except RuntimeError:
            # No running loop - call shutdown synchronously
            try:
                exec_ref.shutdown(True)
            except Exception:
                logger.exception("Failed to shutdown broker adapter executor")
        finally:
            # Remove reference to executor
            try:
                del self._executor
            except Exception:
                # Ignore failures during best-effort cleanup; executor is already shut down.
                pass
        # Also attempt to shutdown the underlying sync Broker's legacy executor
        broker_exec = getattr(self._broker, "_executor", None)
        if broker_exec is not None:
            try:
                loop = asyncio.get_running_loop()
                await loop.run_in_executor(None, broker_exec.shutdown, True)
            except RuntimeError:
                # No running loop - call synchronously
                try:
                    broker_exec.shutdown(True)
                except Exception:
                    logger.exception("Failed to shutdown underlying Broker executor")
            finally:
                # Prefer a simple, safe reset to None instead of deleting the
                # attribute. Deleting attributes can raise in odd cases
                # (properties, slots, proxies). Setting to `None` satisfies
                # type-checkers and avoids fragile nested exception logic.
                try:
                    if hasattr(self._broker, "_executor"):
                        try:
                            setattr(self._broker, "_executor", None)
                        except Exception:
                            # Fallback to deletion only if setting failed.
                            try:
                                delattr(self._broker, "_executor")
                            except Exception:
                                pass
                except Exception:
                    # Best-effort only; don't raise from close()
                    pass

    async def __aenter__(self) -> "AsyncBrokerAdapter":
        return self

    async def __aexit__(
        self,
        exc_type: Optional[type],
        exc: Optional[BaseException],
        tb: Optional[Any],
    ) -> None:
        await self.close()

    def __del__(self) -> None:
        # Best-effort synchronous shutdown to avoid dangling threads at interpreter exit.
        exec_ref = getattr(self, "_executor", None)
        if exec_ref is not None:
            try:
                exec_ref.shutdown(wait=False)
            except Exception:
                # Avoid raising in __del__
                pass

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
                await self._metric_inc(f"broker_calls_total{{method={method_name}}}")
                result = await asyncio.wait_for(fut, timeout=timeout)
                dur = int((time.time() - start) * 1000)
                logger.info(
                    "broker_call method=%s duration_ms=%d outcome=success",
                    method_name,
                    dur,
                )
                await self._metric_inc(f"broker_calls_success_total{{method={method_name}}}")
                return result
            except asyncio.TimeoutError:
                last_exc = BrokerTimeoutError(f"{method_name} timed out after {timeout}s")
                await self._metric_inc(f"broker_timeouts_total{{method={method_name}}}")
                logger.warning(
                    "broker_call method=%s duration_ms=%d outcome=timeout",
                    method_name,
                    int((time.time() - start) * 1000),
                )
                # Timeouts are handled as transient for reads only
                if not retry or attempt >= self._max_retries:
                    raise last_exc
            except Exception as e:
                # Detect non-retryable (fatal) errors such as invalid parameters
                # or authentication failures and raise BrokerFatalError so
                # callers can handle them without retries.
                fatal = False
                # Common Python errors that indicate programmer/config issues
                if isinstance(e, (ValueError, TypeError, PermissionError)):
                    fatal = True

                # If underlying sync Broker raised a BrokerError with an
                # authentication/invalid message, treat as fatal.
                if isinstance(e, SyncBrokerError):
                    msg = str(e).lower()
                    if any(
                        k in msg
                        for k in (
                            "auth",
                            "authentication",
                            "invalid",
                            "unauthor",
                            "forbidden",
                            "permission",
                        )
                    ):
                        fatal = True

                if fatal:
                    last_exc = BrokerFatalError(str(e))
                    await self._metric_inc(f"broker_fatals_total{{method={method_name}}}")
                    logger.error(
                        "broker_call method=%s duration_ms=%d outcome=fatal exception=%s",
                        method_name,
                        int((time.time() - start) * 1000),
                        e,
                    )
                    raise last_exc

                # Map underlying exceptions to transient taxonomy
                last_exc = BrokerTransientError(str(e))
                await self._metric_inc(f"broker_retries_total{{method={method_name}}}")
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
        cached = await self._cache_get(key)
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
        await self._cache_set(key, res, self._clock_ttl)
        return res

    async def get_account(self) -> Dict[str, Any]:
        key = "get_account"
        cached = await self._cache_get(key)
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
        await self._cache_set(key, res, self._account_ttl)
        return res

    async def get_positions(self) -> List[Dict[str, Any]]:
        key = "get_positions"
        cached = await self._cache_get(key)
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
        await self._cache_set(key, res, self._positions_ttl)
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
