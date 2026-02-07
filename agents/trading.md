# Trading Logic Agent

## Role
Execute trading decisions: strategy evaluation, risk checks, order submission, and reconciliation.

## Responsibilities
1. **Subscribe to BarEvent** from event bus
2. **Evaluate strategy** (SMA crossover, signal generation)
3. **Check risk gates** (kill switch, circuit breaker, market hours, daily limits, spread filter)
4. **Generate OrderIntent** (deterministic client_order_id)
5. **Persist order intent** to SQLite BEFORE submission
6. **Submit order** to broker
7. **Handle order updates** and state transitions
8. **Monitor circuit breaker** and halt on 5 consecutive failures
9. **Run reconciliation** on startup and periodic checks

## Constraints
- **CAN:** Place orders, read market data (via event bus), publish events, write to SQLite
- **CANNOT:** Modify broker connection, change configuration, access credentials
- **MUST:** Persist order intent before submission (crash safety)
- **MUST:** Use fresh `/v2/clock` call before every order (never cached)
- **MUST:** Refuse trade if spread filter enabled and snapshot fetch fails (no bypasses)

## Decision Tree
```
BarEvent received
    ↓
Market open? (fresh /v2/clock call)
    ↓
Kill switch active? → REFUSE
    ↓
Circuit breaker tripped (≥5 failures)? → REFUSE
    ↓
Daily loss limit exceeded? → REFUSE
    ↓
Daily trade count exceeded? → REFUSE
    ↓
Strategy signal generated (BUY/SELL)? → YES
    ↓
Spread filter enabled? → Fetch snapshot, check spread
    ↓ (spread OK or disabled)
Generate deterministic client_order_id
    ↓
Persist OrderIntent to SQLite
    ↓
Submit to broker
    ↓
Publish OrderUpdateEvent
    ↓
Track on circuit breaker
```

## Output Events
```json
{
  "event_type": "OrderIntentEvent",
  "symbol": "AAPL",
  "side": "BUY",
  "qty": 10,
  "client_order_id": "abc123def456",
  "timestamp": "2026-02-05T20:05:00Z"
}
```

## Key Files
- `src/strategy/sma_crossover.py` - Strategy signal generation
- `src/risk_manager.py` - Risk gate enforcement
- `src/order_manager.py` - Order submission and tracking
- `src/reconciliation.py` - Account state validation
- `src/housekeeping.py` - Periodic maintenance
- `src/logger.py` - Unified logging
- `main.py` - Entry point
