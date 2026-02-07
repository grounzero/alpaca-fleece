# Rate Limit Protection Strategy

## Problem

When streaming 31 symbols with WebSocket, aggressive reconnection attempts hit Alpaca's rate limits:
- **HTTP 429** — Too Many Requests
- **Connection limit exceeded** — WebSocket connection pool full

This happens because:
1. Each symbol subscription creates a WebSocket connection
2. On network hiccups, alpaca-py tries to reconnect immediately
3. Multiple rapid reconnections → HTTP 429 from Alpaca
4. Bot gets stuck in reconnect loop, can't recover

## Solution

### 1. **RateLimiter Class** (`src/rate_limiter.py`)

Implements exponential backoff with smart retry logic:

```python
# Tracks failures and enforces backoff
limiter = RateLimiter(
    base_delay=2.0,      # Start with 2s wait
    max_delay=120.0,     # Cap at 2 minutes
    max_retries=5        # Give up after 5 failures
)

# Backoff progression:
# Failure 1: wait 2^1 = 2 seconds
# Failure 2: wait 2^2 = 4 seconds
# Failure 3: wait 2^3 = 8 seconds
# Failure 4: wait 2^4 = 16 seconds
# Failure 5: wait 2^5 = 32 seconds (then give up)
```

### 2. **Stream Rate Limiting** (`src/stream.py`)

- Each stream (market data, trades) has its own `RateLimiter`
- On `ValueError` with "429" or "connection limit":
  - Record failure
  - Calculate backoff delay
  - Wait before retrying
- After 5 failures:
  - Stop retrying
  - Call disconnect callback
  - Log error

### 3. **Behavior**

```
Initial connection OK
    ↓
Bar arrives ✓
    ↓
Network blip → disconnect detected
    ↓
Attempt reconnect 1: FAIL (HTTP 429)
    → Record failure, wait 2s
    ↓
Attempt reconnect 2: FAIL (HTTP 429)
    → Record failure, wait 4s
    ↓
Attempt reconnect 3: FAIL (HTTP 429)
    → Record failure, wait 8s
    ↓
Attempt reconnect 4: SUCCESS ✓
    → Reset failure counter
    → Resume streaming
```

## Configuration

Edit `src/stream.py` to adjust backoff:

```python
self.market_rate_limiter = RateLimiter(
    base_delay=2.0,      # Initial wait (seconds)
    max_delay=120.0,     # Maximum wait (2 minutes)
    max_retries=5,       # Give up after this many failures
)
```

### Recommendations by Scenario

| Scenario | base_delay | max_delay | max_retries |
|----------|-----------|-----------|-------------|
| **Stable network** (home) | 2s | 60s | 10 |
| **Flaky network** (wifi) | 5s | 120s | 5 |
| **Production** (cloud) | 1s | 300s | 20 |

## Monitoring

Check rate limiter status in logs:

```
2026-02-05 21:15:42,123 [WARNING] stream: Market stream: HTTP 429 rate limit hit
2026-02-05 21:15:42,123 [WARNING] stream: Market stream: Rate limited, waiting 4.2s before retry
2026-02-05 21:15:46,325 [INFO] stream: Market stream reconnected (rate limiter reset)
```

## Why This Matters

1. **Prevents cascade failures** — One hiccup won't trigger 50 reconnects
2. **Respects API limits** — Alpaca can process requests cleanly
3. **Self-healing** — Bot recovers gracefully when network returns
4. **Transparent** — No changes needed in order/trading logic

## Future Improvements

1. **Adaptive backoff** — Adjust delays based on success rate
2. **Metrics collection** — Track rate limit hits per day
3. **Circuit breaker** — Stop trading if rate limit persists >5min
4. **Fallback strategies** — HTTP polling if WebSocket keeps failing

