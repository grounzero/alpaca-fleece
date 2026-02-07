# Exit Manager Implementation Plan

## Overview

The Exit Manager is a critical missing component for the Alpaca Fleece trading bot. It automatically manages position exits through profit-taking (+2%) and stop-loss (-1%) mechanisms, monitoring all open positions continuously and submitting exit orders when thresholds are breached.

**Safety Warning:** The current bot has no automated exit protection. Positions will run indefinitely until manual close or opposite signal. Do not deploy to live trading without Exit Manager.

---

## 1. Architecture Overview

### 1.1 Position in the System

```
Phase 3: Trading Logic
  ├── Strategy (SMACrossover) — generates entry signals
  ├── Risk Manager — validates entry signals  
  ├── Order Manager — submits entry orders
  └── Exit Manager — monitors positions, submits exit orders [NEW]
```

### 1.2 Responsibilities

| Responsibility | Description |
|---------------|-------------|
| Position Monitoring | Track all open positions and current market prices |
| Threshold Evaluation | Calculate unrealised P&L against configured thresholds |
| Exit Order Submission | Submit sell orders when profit or loss thresholds breached |
| Duplicate Prevention | Prevent multiple exit orders for same position |
| Partial Fill Handling | Gracefully handle partial fills and resubmit if needed |
| State Persistence | Track exit order state in SQLite for crash safety |

### 1.3 Integration Points

| Component | Interaction |
|-----------|-------------|
| `Broker` | Fetch positions, submit exit orders |
| `StateStore` | Persist exit intents, track pending exits |
| `EventBus` | Subscribe to price updates via BarEvent |
| `OrderManager` | Shared order submission patterns |

---

## 2. Configuration Schema

### 2.1 Additions to `trading.yaml`

```yaml
# =============================================================================
# EXIT MANAGER (Profit Taking & Stop Loss)
# =============================================================================
exit_manager:
  enabled: true
  
  profit_taking:
    enabled: true
    threshold_pct: 2.0           # Close at +2% profit
    order_type: market           # market | limit
    time_in_force: day
    
  stop_loss:
    enabled: true
    threshold_pct: 1.0           # Close at -1% loss
    order_type: market
    time_in_force: day
    
  partial_fills:
    resubmit_delay_seconds: 30
    max_resubmit_attempts: 3
    
  exit_cooldown_seconds: 300     # 5 minutes
```

### 2.2 Validation Rules

- `threshold_pct > 0` required
- Profit threshold should be > stop threshold (warn if not)
- Missing config uses sensible defaults

---

## 3. Data Model

### 3.1 New SQLite Tables

#### `exit_orders` Table

```sql
CREATE TABLE IF NOT EXISTS exit_orders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    client_order_id TEXT NOT NULL UNIQUE,
    symbol TEXT NOT NULL,
    side TEXT NOT NULL,                    -- 'sell'
    qty NUMERIC(10, 4) NOT NULL,
    filled_qty NUMERIC(10, 4) DEFAULT 0,
    avg_fill_price NUMERIC(10, 4),
    exit_type TEXT NOT NULL,               -- 'profit_take' | 'stop_loss'
    entry_price NUMERIC(10, 4) NOT NULL,
    target_price NUMERIC(10, 4) NOT NULL,
    threshold_pct NUMERIC(5, 2) NOT NULL,
    status TEXT NOT NULL,                  -- 'pending' | 'submitted' | 'filled' | 'rejected'
    alpaca_order_id TEXT,
    resubmit_count INTEGER DEFAULT 0,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    filled_at_utc TEXT
);

CREATE INDEX idx_exit_orders_symbol_status ON exit_orders(symbol, status);
CREATE INDEX idx_exit_orders_client_order_id ON exit_orders(client_order_id);
```

#### `position_tracking` Table

```sql
CREATE TABLE IF NOT EXISTS position_tracking (
    symbol TEXT PRIMARY KEY,
    qty NUMERIC(10, 4) NOT NULL,
    avg_entry_price NUMERIC(10, 4) NOT NULL,
    current_price NUMERIC(10, 4),
    unrealised_pnl_pct NUMERIC(6, 2),
    highest_price NUMERIC(10, 4),
    exit_order_id TEXT,
    status TEXT NOT NULL,                  -- 'monitoring' | 'exit_pending' | 'exited'
    last_evaluated_at_utc TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
```

---

## 4. Interface Specification

### 4.1 Class Definition

```python
class ExitType(str, Enum):
    PROFIT_TAKE = "profit_take"
    STOP_LOSS = "stop_loss"

class ExitStatus(str, Enum):
    PENDING = "pending"
    SUBMITTED = "submitted"
    PARTIAL = "partial"
    FILLED = "filled"
    REJECTED = "rejected"

class ExitManager:
    def __init__(
        self,
        broker: Broker,
        state_store: StateStore,
        event_bus: EventBus,
        config: dict,
        strategy_name: str,
    ) -> None:
        """Initialise exit manager with configuration."""
        
    async def start(self) -> None:
        """Start monitoring positions."""
        
    async def stop(self) -> None:
        """Stop monitoring and clean up."""
        
    async def _on_bar(self, event: BarEvent) -> None:
        """Process bar event for price updates."""
        
    async def _evaluate_exit_conditions(
        self, symbol: str, current_price: Decimal, unrealised_pct: Decimal
    ) -> None:
        """Check if exit conditions met and submit exit if so."""
        
    async def _submit_exit(...) -> bool:
        """Submit exit order for position."""
        
    def _generate_exit_order_id(...) -> str:
        """Generate deterministic client_order_id (SHA-256)."""
```

---

## 5. Implementation Phases

### Phase 1: Foundation (2-3 days)

| Task | Hours |
|------|-------|
| Create Exit Manager module structure | 2 |
| Define enums (ExitType, ExitStatus) | 1 |
| Add configuration to trading.yaml | 1 |
| Update config validation | 2 |
| Unit tests for config | 2 |

**Deliverable:** Exit Manager skeleton with configuration

### Phase 2: Data Layer (2-3 days)

| Task | Hours |
|------|-------|
| Create exit_orders table schema | 2 |
| Create position_tracking table | 2 |
| Implement StateStore methods | 4 |
| Unit tests for persistence | 4 |

**Deliverable:** Complete data persistence layer

### Phase 3: Core Logic (3-4 days)

| Task | Hours |
|------|-------|
| Implement __init__ with config parsing | 2 |
| Implement start/stop lifecycle | 2 |
| Implement position syncing | 3 |
| Implement _on_bar handler | 3 |
| Implement threshold evaluation | 3 |
| Implement exit submission | 4 |
| Implement order update handling | 3 |

**Deliverable:** Working Exit Manager core

### Phase 4: Integration & Testing (2-3 days)

| Task | Hours |
|------|-------|
| Wire into orchestrator.py | 2 |
| Integration tests | 4 |
| Edge case testing (partial fills, etc.) | 3 |
| Update documentation | 2 |

**Deliverable:** Production-ready Exit Manager

**Total Estimated Effort:** 9-13 days

---

## 6. Event Flow

```
BarEvent received from EventBus
    ↓
Update position current_price
    ↓
Calculate unrealised P&L %
    ↓
Check thresholds (profit >= 2% or loss <= -1%)
    ↓
Generate deterministic client_order_id
    ↓
Persist exit intent to SQLite (status='pending')
    ↓
Submit sell order via Broker
    ↓
Update status to 'submitted'
    ↓
OrderUpdateEvent received (fill/partial/reject)
    ↓
Update exit_orders table
    ↓
If filled: remove from tracking, set cooldown
```

---

## 7. Risk Considerations

### High Severity

| Risk | Mitigation |
|------|------------|
| Exit order fails to submit | Circuit breaker counts failures; state persists for retry |
| Partial fill leaves position open | Resubmit remaining quantity (max 3 attempts) |
| Market gaps past stop | Market orders execute at market price (slippage accepted) |
| Duplicate exits | Deterministic client_order_id prevents duplicates |

### Medium Severity

| Risk | Mitigation |
|------|------------|
| Price volatility triggers false exits | Configurable thresholds; cooldown period |
| Race condition: exit + entry signal | Exit takes precedence; cooldown prevents re-entry |
| Position changes while exit pending | Recheck position size before resubmit |

---

## 8. Testing Strategy

### Unit Tests

```python
def test_exit_triggered_on_profit_threshold()
def test_exit_triggered_on_stop_threshold()
def test_no_exit_when_threshold_not_met()
def test_duplicate_exit_prevented()
def test_partial_fill_handling()
def test_cooldown_prevents_re_entry()
def test_deterministic_order_id()
```

### Integration Tests

```python
def test_full_exit_flow_profit_take()
def test_full_exit_flow_stop_loss()
def test_position_sync_on_startup()
def test_order_update_processing()
```

### Edge Cases

- Position opened while bot offline
- Partial fill then price moves
- Exit rejected by broker
- Multiple positions, one hits threshold
- Market closes while exit pending

---

## 9. Implementation Checklist

### Phase 1
- [ ] Create `src/exit_manager.py` module
- [ ] Define ExitType, ExitStatus enums
- [ ] Add exit_manager section to `config/trading.yaml`
- [ ] Update `src/config.py` validation
- [ ] Write config unit tests

### Phase 2
- [ ] Create `exit_orders` table in `state_store.py`
- [ ] Create `position_tracking` table
- [ ] Implement `save_exit_order()`
- [ ] Implement `get_exit_order()` methods
- [ ] Implement position tracking methods
- [ ] Write persistence unit tests

### Phase 3
- [ ] Implement ExitManager.__init__()
- [ ] Implement start()/stop() lifecycle
- [ ] Implement position syncing
- [ ] Implement _on_bar() handler
- [ ] Implement threshold evaluation
- [ ] Implement _submit_exit()
- [ ] Implement order update handling
- [ ] Handle partial fills

### Phase 4
- [ ] Wire into orchestrator.py
- [ ] Add integration tests
- [ ] Test edge cases
- [ ] Update documentation
- [ ] Run full test suite (128 tests)

---

## 10. Safety Checklist Before Live Trading

- [ ] Exit Manager enabled in config
- [ ] Profit threshold configured (recommend 2%)
- [ ] Stop loss threshold configured (recommend 1%)
- [ ] All exit order unit tests passing
- [ ] Integration tests passing
- [ ] Tested on paper trading with real positions
- [ ] Verified exit orders submit correctly
- [ ] Verified partial fill handling works
- [ ] Verified cooldown period prevents re-entry

---

## Summary

The Exit Manager is essential for safe live trading. Without it, positions have no automated protection and will run indefinitely.

**Estimated effort:** 9-13 days  
**Critical for live trading:** Yes  
**Can trade paper without it:** Yes (with monitoring)  

**Recommended priority:** Implement before any live trading deployment.
