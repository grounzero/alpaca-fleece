# Parallel Processing Analysis Report: Alpaca-Fleece Polling Stream

## Executive Summary

The current polling implementation in `src/stream_polling.py` processes 31 symbols sequentially, utilising approximately 31 requests per minute out of the Alpaca free tier allowance of 200 requests per minute (15.5% utilisation). This leaves significant headroom for capacity expansion through parallel processing techniques.

**Key Finding**: The Alpaca SDK natively supports batch requests via `symbol_or_symbols: Union[str, List[str]]`, enabling multiple symbols per API call. This is the most efficient path to increased capacity.

**Recommended Approach**: Implement **Option 2 (Batch API Requests)** as the primary enhancement, with **Option 1 (asyncio.gather())** as a secondary optimisation for latency-sensitive scenarios. This hybrid approach could increase symbol capacity from 31 to **500+ symbols** without exceeding rate limits.

---

## Current State Analysis

### Implementation Review

**File**: `src/stream_polling.py`

**Current Flow**:
```python
async def _poll_loop(self) -> None:
    while True:
        for symbol in self._symbols:  # Sequential processing
            await self._poll_symbol(symbol)  # One symbol per call
        await asyncio.sleep(sleep_seconds)

async def _poll_symbol(self, symbol: str) -> None:
    request = StockBarsRequest(
        symbol_or_symbols=symbol,  # Single symbol only
        timeframe=TimeFrame.Minute,
        limit=2,
    )
    bars = self.client.get_stock_bars(request)  # Synchronous call
```

### Performance Characteristics

| Metric | Current Value | Limit | Utilisation |
|--------|---------------|-------|-------------|
| Symbols polled | 31 | — | — |
| Requests per minute | ~31 | 200 | 15.5% |
| Request pattern | Sequential | — | — |
| Batch size | 1 symbol | Unlimited* | — |
| Polling interval | ~60 seconds | — | — |

*Alpaca documentation suggests practical limits of ~100 symbols per batch for optimal performance.

### Bottlenecks Identified

1. **Sequential Processing**: The `for symbol in self._symbols` loop blocks on each synchronous API call
2. **Single-Symbol Requests**: `symbol_or_symbols=symbol` prevents utilising the batch API capability
3. **Synchronous Client**: `StockHistoricalDataClient.get_stock_bars()` is a blocking call within an async context
4. **No Request Batching**: Existing `batch_iter()` utility (in `stream.py`) is not utilised

---

## Option 1: asyncio.gather() for Concurrent Symbol Polling

### Implementation Approach

Utilise `asyncio.gather()` to execute multiple `_poll_symbol()` coroutines concurrently while maintaining single-symbol requests.

```python
import asyncio
from concurrent.futures import ThreadPoolExecutor

class StreamPolling:
    def __init__(self, ...):
        # ... existing init ...
        self._executor = ThreadPoolExecutor(max_workers=10)
    
    async def _poll_loop(self) -> None:
        while True:
            try:
                # Create tasks for all symbols
                tasks = [
                    self._poll_symbol(symbol) 
                    for symbol in self._symbols
                ]
                
                # Execute concurrently with semaphore for rate limiting
                await asyncio.gather(*tasks, return_exceptions=True)
                
                await self._sleep_until_next_minute()
                
            except Exception as e:
                logger.error(f"Polling error: {e}")
                await asyncio.sleep(5)
    
    async def _poll_symbol(self, symbol: str) -> None:
        """Poll symbol using thread pool for sync SDK call."""
        try:
            loop = asyncio.get_event_loop()
            
            # Run blocking SDK call in thread pool
            bars = await loop.run_in_executor(
                self._executor,
                self._fetch_bars_sync,
                symbol
            )
            
            await self._process_bars(symbol, bars)
            
        except Exception as e:
            logger.warning(f"Polling error for {symbol}: {e}")
    
    def _fetch_bars_sync(self, symbol: str):
        """Synchronous wrapper for SDK call."""
        request = StockBarsRequest(
            symbol_or_symbols=symbol,
            timeframe=TimeFrame.Minute,
            limit=2,
        )
        return self.client.get_stock_bars(request)
```

### Pros

| Advantage | Description |
|-----------|-------------|
| **Minimal Code Change** | Existing `_poll_symbol()` logic largely preserved |
| **I/O Parallelism** | Overlaps network latency across multiple requests |
| **Incremental Adoption** | Can be enabled via configuration flag |
| **Granular Error Handling** | Individual symbol failures don't block others |
| **Familiar Pattern** | `asyncio.gather()` is idiomatic Python |

### Cons

| Disadvantage | Description |
|--------------|-------------|
| **Thread Pool Overhead** | Each request still requires a thread for the sync SDK call |
| **Rate Limit Risk** | Concurrent requests may burst and trigger 429 errors |
| **Diminishing Returns** | Benefits plateau as network overhead dominates |
| **Complex Error Aggregation** | `return_exceptions=True` requires careful result handling |
| **Resource Utilisation** | Thread pools consume memory and context-switching overhead |

### Estimated Capacity Increase

| Configuration | Symbols Supported | Requests/Min | Notes |
|---------------|-------------------|--------------|-------|
| Current (sequential) | 31 | 31 | Baseline |
| 5 concurrent workers | ~100 | 100 | Within rate limit |
| 10 concurrent workers | ~150 | 150 | 75% rate limit utilisation |
| 15 concurrent workers | ~200 | 200 | At rate limit (risky) |

**Theoretical Maximum**: ~200 symbols (bounded by rate limit, not parallelism)

---

## Option 2: Batch API Requests (Recommended Primary)

### Implementation Approach

Leverage the Alpaca SDK's native support for multiple symbols per request via `symbol_or_symbols: Union[str, List[str]]`. Utilise the existing `batch_iter()` utility from `stream.py`.

```python
from itertools import islice
from typing import List

class StreamPolling:
    def __init__(self, ..., batch_size: int = 25):
        # ... existing init ...
        self.batch_size = batch_size
    
    async def _poll_loop(self) -> None:
        while True:
            try:
                # Process symbols in batches
                for batch in self._batch_iter(self._symbols, self.batch_size):
                    await self._poll_batch(batch)
                
                await self._sleep_until_next_minute()
                
            except Exception as e:
                logger.error(f"Polling error: {e}")
                await asyncio.sleep(5)
    
    async def _poll_batch(self, symbols: List[str]) -> None:
        """Poll multiple symbols in a single API request."""
        try:
            loop = asyncio.get_event_loop()
            
            # Run blocking SDK call in thread pool
            bars_by_symbol = await loop.run_in_executor(
                None,  # Uses default executor
                self._fetch_batch_sync,
                symbols
            )
            
            # Process results for all symbols
            for symbol, bar_list in bars_by_symbol.items():
                if bar_list:
                    await self._process_bar(symbol, bar_list[-1])
                    
        except Exception as e:
            logger.warning(f"Batch polling error for {symbols}: {e}")
    
    def _fetch_batch_sync(self, symbols: List[str]):
        """Synchronous batch fetch for multiple symbols."""
        request = StockBarsRequest(
            symbol_or_symbols=symbols,  # List of symbols
            timeframe=TimeFrame.Minute,
            limit=2,
        )
        return self.client.get_stock_bars(request)
    
    def _batch_iter(self, iterable, batch_size: int):
        """Yield successive batches (existing utility from stream.py)."""
        iterator = iter(iterable)
        while batch := list(islice(iterator, batch_size)):
            yield batch
```

### Batch Size Recommendations

| Batch Size | Requests for 200 Symbols | Latency | Use Case |
|------------|--------------------------|---------|----------|
| 10 | 20 requests | Low | Conservative, fast response |
| 25 | 8 requests | Medium | **Balanced (recommended)** |
| 50 | 4 requests | Higher | Aggressive, fewer API calls |
| 100 | 2 requests | Highest | Maximum efficiency |

### Pros

| Advantage | Description |
|-----------|-------------|
| **Maximum Efficiency** | Single API call retrieves multiple symbols |
| **Rate Limit Optimisation** | Dramatically reduces request count |
| **SDK Native** | Uses Alpaca's intended batch interface |
| **Lower Overhead** | Fewer HTTP connections, less serialisation |
| **Scalable** | Capacity increases linearly with batch size |
| **Existing Test Coverage** | `test_symbol_batching.py` validates batching logic |

### Cons

| Disadvantage | Description |
|--------------|-------------|
| **All-or-Nothing Failure** | One error affects entire batch |
| **Response Size** | Large batches return more data (memory consideration) |
| **Rate Limit Complexity** | Need to track per-request vs per-symbol limits |
| **Debugging Complexity** | Harder to isolate problematic symbols |

### Estimated Capacity Increase

| Batch Size | Symbols Supported | Requests/Min | Efficiency vs Current |
|------------|-------------------|--------------|----------------------|
| 10 | 200 | 20 | **6.5x increase** |
| 25 | 500 | 20 | **16x increase** |
| 50 | 1,000 | 20 | **32x increase** |
| 100 | 2,000 | 20 | **64x increase** |

**Practical Maximum**: 500-1,000 symbols (depending on memory and processing latency)

---

## Option 3: Hybrid Approach (Recommended Overall)

### Implementation Approach

Combine batch requests with concurrent batch processing for optimal throughput and resilience.

```python
import asyncio
from typing import List
import time

class StreamPolling:
    def __init__(
        self, 
        ..., 
        batch_size: int = 25,
        max_concurrent_batches: int = 4,
        rate_limit_rpm: int = 180  # Conservative: 90% of 200
    ):
        # ... existing init ...
        self.batch_size = batch_size
        self.max_concurrent_batches = max_concurrent_batches
        self.rate_limit_rpm = rate_limit_rpm
        self._semaphore = asyncio.Semaphore(max_concurrent_batches)
        self._request_times = []  # Track request timestamps
    
    async def _poll_loop(self) -> None:
        while True:
            try:
                # Create batches
                batches = list(self._batch_iter(self._symbols, self.batch_size))
                
                # Process batches with controlled concurrency
                tasks = [
                    self._poll_batch_with_rate_limit(batch) 
                    for batch in batches
                ]
                
                await asyncio.gather(*tasks, return_exceptions=True)
                
                await self._sleep_until_next_minute()
                
            except Exception as e:
                logger.error(f"Polling error: {e}")
                await asyncio.sleep(5)
    
    async def _poll_batch_with_rate_limit(self, symbols: List[str]) -> None:
        """Poll batch respecting rate limits."""
        async with self._semaphore:  # Limit concurrent batches
            await self._respect_rate_limit()
            
            try:
                loop = asyncio.get_event_loop()
                bars_by_symbol = await loop.run_in_executor(
                    None,
                    self._fetch_batch_sync,
                    symbols
                )
                
                self._record_request()
                
                # Process each symbol in batch
                for symbol, bar_list in bars_by_symbol.items():
                    if bar_list:
                        await self._process_bar(symbol, bar_list[-1])
                        
            except Exception as e:
                logger.warning(f"Batch error for {symbols}: {e}")
                # Consider: fallback to individual symbol polling
    
    async def _respect_rate_limit(self) -> None:
        """Ensure we don't exceed rate limit."""
        now = time.time()
        minute_ago = now - 60
        
        # Remove old timestamps
        self._request_times = [t for t in self._request_times if t > minute_ago]
        
        # If at limit, wait
        if len(self._request_times) >= self.rate_limit_rpm:
            sleep_time = 60 - (now - self._request_times[0]) + 0.1
            if sleep_time > 0:
                logger.debug(f"Rate limit reached, sleeping {sleep_time:.1f}s")
                await asyncio.sleep(sleep_time)
    
    def _record_request(self) -> None:
        """Record request timestamp for rate limiting."""
        self._request_times.append(time.time())
```

### Configuration Matrix

| Symbols | Batch Size | Concurrent Batches | Requests/Min | Latency |
|---------|------------|-------------------|--------------|---------|
| 31 | 10 | 2 | 4 | Low |
| 100 | 25 | 4 | 4 | Low |
| 200 | 25 | 4 | 8 | Medium |
| 500 | 50 | 4 | 10 | Medium |
| 1,000 | 50 | 4 | 20 | Medium |

### Pros

| Advantage | Description |
|-----------|-------------|
| **Optimal Throughput** | Balances batch efficiency with concurrent I/O |
| **Resilient** | Semaphore prevents overwhelming the API |
| **Adaptive** | Rate limit tracking adjusts to actual usage |
| **Production-Ready** | Built-in safety mechanisms |
| **Fallback Ready** | Can degrade to single-symbol on batch failure |

### Cons

| Disadvantage | Description |
|--------------|-------------|
| **Complexity** | Most sophisticated implementation |
| **Tuning Required** | Batch size and concurrency need calibration |
| **Monitoring Needed** | Rate limit tracking adds operational overhead |

---

## Recommendation

### Immediate Implementation (Priority 1)

**Implement Option 2 (Batch API Requests)** with the following parameters:

```python
# Recommended configuration
batch_size = 25  # 8 requests for 200 symbols
polling_interval = 60  # seconds
```

**Rationale**:
- 16x capacity increase (31 → 500 symbols)
- Minimal code complexity
- Uses existing tested utilities (`batch_iter`)
- Well within rate limits (8 requests vs 200 limit)
- Easy rollback if issues arise

### Secondary Enhancement (Priority 2)

**Implement Option 3 (Hybrid)** if:
- Processing 500+ symbols
- Sub-60-second polling required
- Latency-sensitive strategies deployed

### What to Avoid

- **Pure Option 1** without batching: Doesn't address the fundamental inefficiency
- **Batch sizes > 100**: Diminishing returns and increased failure blast radius
- **Unconstrained concurrency**: Risk of rate limit violations

---

## Testing Plan

### Unit Tests

```python
# tests/test_stream_polling_parallel.py

class TestBatchPolling:
    """Tests for batch-based polling implementation."""
    
    def test_batch_size_calculation(self):
        """Verify batch sizing logic."""
        symbols = [f"SYM{i}" for i in range(31)]
        batches = list(batch_iter(symbols, 10))
        assert len(batches) == 4  # 3×10 + 1×1
    
    def test_batch_request_formation(self):
        """Verify StockBarsRequest receives list of symbols."""
        batch = ["AAPL", "MSFT", "GOOGL"]
        request = StockBarsRequest(
            symbol_or_symbols=batch,
            timeframe=TimeFrame.Minute,
            limit=2
        )
        assert request.symbol_or_symbols == batch
    
    @pytest.mark.asyncio
    async def test_concurrent_batch_processing(self):
        """Verify semaphore limits concurrent batches."""
        polling = StreamPolling(..., max_concurrent_batches=2)
        assert polling._semaphore._value == 2
```

### Integration Tests

```python
class TestPollingCapacity:
    """Integration tests for capacity limits."""
    
    @pytest.mark.slow
    @pytest.mark.asyncio
    async def test_200_symbols_within_rate_limit(self):
        """Verify 200 symbols don't exceed 200 requests/minute."""
        symbols = [f"SYM{i}" for i in range(200)]
        polling = StreamPolling(..., batch_size=25)
        
        with mock.patch.object(polling, '_record_request') as mock_record:
            await polling._poll_loop_once()  # One iteration
            
        # Should make ~8 requests for 200 symbols with batch_size=25
        assert mock_record.call_count <= 8
    
    @pytest.mark.asyncio
    async def test_batch_failure_fallback(self):
        """Verify graceful handling of batch request failures."""
        # Simulate API error and verify retry or fallback
```

### Load Testing

```python
# scripts/load_test_polling.py

async def load_test_symbols(symbol_count: int, batch_size: int):
    """Measure polling latency for symbol set."""
    symbols = [f"SYM{i}" for i in range(symbol_count)]
    
    polling = StreamPolling(..., batch_size=batch_size)
    
    start = time.time()
    await polling._poll_all_symbols()  # One complete poll cycle
    elapsed = time.time() - start
    
    print(f"Symbols: {symbol_count}, Batch: {batch_size}, Time: {elapsed:.2f}s")
    return elapsed

# Test configurations
test_cases = [
    (31, 1),    # Current baseline
    (31, 10),   # Batched
    (100, 10),  # Scaled
    (100, 25),  # Scaled + larger batch
    (200, 25),  # Double capacity
]
```

### Validation Checklist

- [ ] Batch requests return correct data for all symbols
- [ ] Rate limit tracking is accurate
- [ ] Error handling doesn't stall the polling loop
- [ ] Memory usage remains stable with large symbol sets
- [ ] Graceful degradation on API errors
- [ ] Backwards compatible with single-symbol configuration

---

## Risk Assessment

### High Severity

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **Batch API Failure** | Low | High | Implement fallback to single-symbol polling; circuit breaker pattern |
| **Rate Limit Breach** | Medium | High | Conservative batch sizing (90% of limit); request tracking |
| **Memory Exhaustion** | Low | Medium | Limit batch size to 50; monitor BarSet response sizes |

### Medium Severity

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **Increased Latency** | Medium | Medium | Async processing; configurable batch sizes |
| **Error Isolation** | Medium | Medium | Per-batch error handling; detailed logging |
| **Configuration Complexity** | High | Low | Sensible defaults; configuration validation |

### Low Severity

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **SDK Version Incompatibility** | Low | Medium | Pin SDK version; test on upgrade |
| **Symbol Validation** | Medium | Low | Pre-validate symbol lists; handle 404s gracefully |

### Rollback Plan

1. **Configuration Flag**: Implement `USE_BATCH_POLLING` environment variable
2. **Feature Toggle**: Can disable batching at runtime
3. **Version Pin**: Tag pre-batch release for quick rollback
4. **Monitoring Alert**: Alert if polling latency > 30 seconds

---

## Conclusion

The current polling implementation has significant room for improvement. By implementing **Option 2 (Batch API Requests)** with a batch size of 25, the system can scale from 31 symbols to 500+ symbols—a **16x capacity increase**—while actually **reducing** the number of API calls from 31 to 8 per minute.

The existing `batch_iter()` utility in `stream.py` and comprehensive test coverage in `test_symbol_batching.py` provide a solid foundation for this enhancement. The Alpaca SDK's native support for multi-symbol requests makes this a low-risk, high-impact improvement.

For future expansion beyond 500 symbols, the **Hybrid Approach (Option 3)** provides a clear path to supporting 1,000+ symbols with appropriate rate limit management and concurrent batch processing.

---

*Report prepared for Alpaca-Fleece Engineering Team*
*Analysis date: 2026-02-07*
