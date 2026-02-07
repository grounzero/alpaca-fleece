# Code Review Fix Plan

## Overview

This document outlines the fixes required to address code review comments for the alpaca-fleece trading bot. Issues are prioritized by severity (Critical > Medium > Low).

---

## Priority Rankings

| Priority | Issue | Severity |
|----------|-------|----------|
| 1 | Strategy Interface Mismatch | Critical |
| 2 | Decimal/Float Type Mismatch | Medium |
| 3 | Stream Batch Delay Off-by-One | Medium |
| 4 | Weak Test Assertions | Medium |
| 5 | CI Python Version Mismatch | Low |
| 6 | Private SDK API Usage | Low |

---

## Issue 1: Strategy Interface Mismatch (Critical)

### Problem
BaseStrategy abstract interface does not match SMACrossover implementation:

| Method | BaseStrategy Declares | SMACrossover Implements |
|--------|----------------------|-------------------------|
| `on_bar` | `-> SignalEvent \| None` | `-> list[SignalEvent]` |
| `get_required_history` | `(self) -> int` | `(self, symbol: str \| None) -> int` |

This violates Liskov Substitution Principle and fails mypy strict checks.

### Files to Modify
1. `src/strategy/base.py` - Update abstract interface
2. `src/strategy/sma_crossover.py` - Minor signature alignment

### Fix Approach

#### Step 1: Update `BaseStrategy` interface
```python
@abstractmethod
def get_required_history(self, symbol: str | None = None) -> int:
    """Minimum bars needed before first signal.
    
    Args:
        symbol: Optional symbol for symbol-aware strategies
    
    Returns:
        Minimum bars required
    """
    pass

@abstractmethod
async def on_bar(self, symbol: str, df: pd.DataFrame) -> list[SignalEvent]:
    """Process bar and emit signals if triggered.
    
    Args:
        symbol: Stock symbol
        df: DataFrame with bars (index=timestamp, columns=open/high/low/close/volume/etc)
    
    Returns:
        List of SignalEvent objects (empty list if no signals)
    """
    pass
```

#### Step 2: Update `SMACrossover.get_required_history` signature
Change from:
```python
def get_required_history(self, symbol: str = None) -> int:
```
To:
```python
def get_required_history(self, symbol: str | None = None) -> int:
```

### Testing Strategy
1. Run mypy strict: `mypy src --strict`
2. Verify all existing tests pass: `pytest tests -v`
3. Create a test ensuring SMACrossover can be used as BaseStrategy:
```python
def test_strategy_interface_compliance():
    """Verify SMACrossover satisfies BaseStrategy interface."""
    store = MockStateStore()
    strategy: BaseStrategy = SMACrossover(store)
    assert hasattr(strategy, 'name')
    assert callable(strategy.get_required_history)
```

### Estimated Effort
- **Time**: 15-20 minutes
- **Complexity**: Low
- **Risk**: Very Low (interface alignment only)

---

## Issue 2: Decimal/Float Type Mismatch (Medium)

### Problem
`qty` parameter typed as `Decimal` in `OrderManager.submit_order()` but passed to:
- `StateStore.save_order_intent(qty: float)`
- `Broker.submit_order(qty: float)`  
- `OrderIntentEvent(qty: float)`

This creates type inconsistency and potential precision issues.

### Files to Modify
1. `src/order_manager.py` - Standardize type handling

### Fix Approach

Two viable options:

#### Option A: Standardize on Float (Recommended)
Change `submit_order` signature to use `float` and convert at module boundary:
```python
async def submit_order(
    self,
    signal: SignalEvent,
    qty: float,  # Changed from Decimal
) -> bool:
    # ... rest of method
    self.state_store.save_order_intent(
        client_order_id=client_order_id,
        symbol=symbol,
        side=side,
        qty=float(qty),  # Ensure float
        status="new",
    )
    # ...
    order = self.broker.submit_order(
        symbol=symbol,
        side=side,
        qty=float(qty),  # Ensure float
        # ...
    )
```

#### Option B: Use Decimal Throughout (Higher Effort)
Would require updating:
- `StateStore` schema to use `Decimal`/`NUMERIC`
- `Broker.submit_order()` signature
- `OrderIntentEvent` dataclass

**Decision**: Use Option A. Float precision is sufficient for order quantities, and Alpaca API uses float. Only use Decimal for high-precision financial calculations (like P&L tracking).

### Testing Strategy
1. Type check: `mypy src/order_manager.py --strict`
2. Unit test with both float and Decimal inputs
3. Verify no precision loss in typical order quantities (1-10000 shares)

### Estimated Effort
- **Time**: 10-15 minutes
- **Complexity**: Low
- **Risk**: Low (type alignment, no logic change)

---

## Issue 3: Stream Batch Delay Off-by-One (Medium)

### Problem
Current code:
```python
if i < len(symbols) // batch_size:
    await asyncio.sleep(batch_delay)
```

This has two issues:
1. When `len(symbols)` is exact multiple of `batch_size`, it sleeps after the final batch
2. Integer division may miss the last batch delay in edge cases

### Files to Modify
1. `src/stream.py` - Fix batch delay logic

### Fix Approach
```python
# Calculate actual number of batches (ceiling division)
num_batches = (len(symbols) + batch_size - 1) // batch_size

for i, batch in enumerate(batch_iter(symbols, batch_size)):
    logger.info(f"Subscribing batch {i+1}: {batch}")
    self.market_data_stream.subscribe_bars(handle_bar, *batch)
    
    # Delay between batches (except after last batch)
    if i < num_batches - 1:  # Fixed: only sleep between batches
        await asyncio.sleep(batch_delay)
```

### Testing Strategy
1. Unit test with exact multiple of batch_size (e.g., 20 symbols, batch_size=10 → 2 batches, 1 sleep)
2. Unit test with non-exact multiple (e.g., 25 symbols, batch_size=10 → 3 batches, 2 sleeps)
3. Verify sleep count equals `num_batches - 1`

### Estimated Effort
- **Time**: 10 minutes
- **Complexity**: Low
- **Risk**: Very Low (logic fix, no API changes)

---

## Issue 4: Weak Test Assertions (Medium)

### Problem
Current test assertions are tautological:
```python
assert has_buy or len(signals) >= 0  # Always true - len() >= 0 is always true
```

This provides no actual test coverage for the crossover logic.

### Files to Modify
1. `tests/test_strategy.py` - Rewrite with deterministic test data

### Fix Approach

#### Step 1: Create synthetic data with guaranteed crossovers
```python
def create_crossover_data(crossover_type: str, bars: int = 60) -> pd.DataFrame:
    """Create DataFrame with guaranteed SMA crossover.
    
    crossover_type: 'upward' or 'downward'
    """
    # Pre-crossover: establish SMA trend
    if crossover_type == 'upward':
        # Fast SMA below slow, then crosses above
        close = list(range(100, 120))  # 20 bars rising
        close.extend(range(120, 150))  # 30 bars faster rise
    else:  # downward
        close = list(range(150, 130, -1))  # 20 bars falling
        close.extend(range(130, 100, -1))  # 30 bars faster fall
    
    return pd.DataFrame({
        "close": close,
    }, index=pd.DatetimeIndex([
        datetime(2024, 1, 1, 10, i, tzinfo=timezone.utc) for i in range(len(close))
    ]))
```

#### Step 2: Rewrite assertions
```python
@pytest.mark.asyncio
async def test_sma_crossover_emits_buy_on_upward_cross(state_store):
    """Strategy should emit BUY signal on upward cross."""
    strategy = SMACrossover(state_store)
    df = create_crossover_data('upward')
    
    signals = await strategy.on_bar("AAPL", df)
    
    # Must have at least one BUY signal
    buy_signals = [s for s in signals if s.signal_type == "BUY"]
    assert len(buy_signals) >= 1, f"Expected at least 1 BUY, got {len(buy_signals)}"
    
    # Verify signal structure
    for signal in buy_signals:
        assert signal.symbol == "AAPL"
        assert "confidence" in signal.metadata
        assert "sma_period" in signal.metadata
```

### Testing Strategy
1. Run all strategy tests: `pytest tests/test_strategy.py -v`
2. Verify assertions fail when code is intentionally broken
3. Achieve 100% branch coverage on signal generation logic

### Estimated Effort
- **Time**: 30-45 minutes
- **Complexity**: Medium (requires understanding SMA crossover math)
- **Risk**: Low (test-only changes)

---

## Issue 5: CI Python Version Mismatch (Low)

### Problem
- `pyproject.toml` specifies: `requires-python = ">=3.12"`
- `.github/workflows/test.yml` tests: `['3.11', '3.12']`

This causes CI to test on an unsupported Python version.

### Files to Modify
1. `.github/workflows/test.yml` - Update matrix

### Fix Approach
```yaml
strategy:
  matrix:
    python-version: ['3.12']  # Removed '3.11'
```

### Testing Strategy
1. Push to branch and verify CI passes
2. Confirm Python 3.11 is no longer in test matrix

### Estimated Effort
- **Time**: 2 minutes
- **Complexity**: Trivial
- **Risk**: None

---

## Issue 6: Private SDK API Usage (Low)

### Problem
Using `StockDataStream._run_forever()` and `TradingStream._run_forever()` which are private APIs (underscore prefix).

### Files to Modify
1. `src/stream.py` - Consider public API alternatives

### Fix Approach

**Note**: This is marked as Low priority because:
1. The SDK may not expose a public async alternative
2. The current implementation works correctly
3. Breaking changes would be caught by tests

#### Investigation Required
Check if `alpaca-py` SDK provides a public async method:
```python
# Check SDK documentation for alternatives like:
await self.market_data_stream.start()  # Does this exist?
await self.market_data_stream.run_async()  # Or this?
```

#### If Public API Available
```python
# Use public API if available
if hasattr(self.market_data_stream, 'run_async'):
    asyncio.create_task(self.market_data_stream.run_async())
else:
    # Fall back to private API with warning
    logger.warning("Using private SDK API _run_forever()")
    asyncio.create_task(self.market_data_stream._run_forever())
```

#### If No Public API
Add comment documenting the decision:
```python
# NOTE: Using _run_forever() because alpaca-py doesn't expose
# a public async equivalent. This is intentional - the sync
# run() method conflicts with our async architecture.
asyncio.create_task(self.market_data_stream._run_forever())  # type: ignore
```

### Testing Strategy
1. Verify stream connectivity still works
2. Test reconnection logic
3. Monitor for SDK deprecation warnings

### Estimated Effort
- **Time**: 15-30 minutes (including SDK research)
- **Complexity**: Low-Medium
- **Risk**: Low (if no changes) to Medium (if switching APIs)

---

## Implementation Order

Recommended order to minimize conflicts:

1. **Issue 5** (CI Python version) - Independent, do first
2. **Issue 1** (Strategy Interface) - Core architecture, affects downstream
3. **Issue 2** (Decimal/Float) - Depends on understanding Issue 1 scope
4. **Issue 3** (Batch delay) - Isolated change
5. **Issue 4** (Test assertions) - Test-only, can be done anytime
6. **Issue 6** (Private API) - Requires investigation, lowest priority

---

## Total Estimated Effort

| Issue | Time | Complexity | Risk |
|-------|------|------------|------|
| 1 - Strategy Interface | 15-20 min | Low | Very Low |
| 2 - Decimal/Float | 10-15 min | Low | Low |
| 3 - Batch Delay | 10 min | Low | Very Low |
| 4 - Test Assertions | 30-45 min | Medium | Low |
| 5 - CI Python | 2 min | Trivial | None |
| 6 - Private API | 15-30 min | Low-Med | Low-Med |
| **TOTAL** | **~1.5-2 hours** | | |

---

## Post-Implementation Checklist

- [ ] All mypy strict checks pass: `mypy src --strict`
- [ ] All tests pass: `pytest tests -v`
- [ ] Coverage maintained at 80%+: `pytest tests --cov=src --cov-fail-under=80`
- [ ] Linting passes: `ruff check src tests`
- [ ] Formatting check: `black --check src tests`
- [ ] CI passes on all supported Python versions
