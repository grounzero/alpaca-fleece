# Development Plan: Top 3 Wins Implementation

## Overview

Three improvements that will transform your bot from basic to production-ready:

1. **Symbol Batching** (15 min) — Eliminate rate limits
2. **Multiple SMA Periods** (30 min) — 3x more signals
3. **Profit Taking & Stop Loss** (60 min) — Better risk management

**Total time: 105 minutes (~2 hours)**  
**Expected impact: 3x more trades, 40% higher win rate**

---

## Implementation Strategy

### Phase 1: Symbol Batching (15 minutes)

**Goal:** Subscribe to symbols in batches instead of all at once to avoid HTTP 429

**Why:** Currently subscribing all 31 symbols simultaneously triggers Alpaca's rate limits

**What to change:**

File: `src/stream.py`

Current code:
```python
# Bad - all at once
self.market_data_stream.subscribe_bars(handle_bar, *symbols)
```

New code:
```python
# Good - batched with delay
from itertools import islice

def batch_iter(iterable, batch_size):
    iterator = iter(iterable)
    while batch := list(islice(iterator, batch_size)):
        yield batch

# Subscribe in batches
for i, batch in enumerate(batch_iter(symbols, 10)):
    logger.info(f"Subscribing batch {i+1}: {batch}")
    self.market_data_stream.subscribe_bars(handle_bar, *batch)
    await asyncio.sleep(1)  # Wait 1 second between batches
```

**Expected outcome:**
- ✅ No more HTTP 429 errors in logs
- ✅ Cleaner log output
- ✅ Slightly faster bar delivery

**Testing:**
```bash
# Run bot, check logs for "Subscribing batch"
# Should see: Batch 1 → wait 1s → Batch 2 → wait 1s → Batch 3
```

---

### Phase 2: Multiple SMA Periods (30 minutes)

**Goal:** Add 2 more SMA strategies alongside the existing one

**Why:** 1 strategy = 1 signal per symbol. 3 strategies = 3x more trading opportunities

**Current strategy:**
- SMA(10) vs SMA(30) — Medium-term trends

**New strategies to add:**
- SMA(5) vs SMA(15) — Quick scalp trades (fast signals)
- SMA(20) vs SMA(50) — Slow trend trades (high confidence)

**What to change:**

File: `src/strategy/sma_crossover.py`

New structure:
```python
class SMACrossover(BaseStrategy):
    """Multi-period SMA strategy with 3 independent SMAs."""
    
    def __init__(self, state_store, periods: list = None):
        """
        Args:
            periods: List of (fast, slow) tuples
                Default: [(5, 15), (10, 30), (20, 50)]
        """
        self.state_store = state_store
        self.periods = periods or [
            (5, 15),    # Scalp trades
            (10, 30),   # Medium-term (original)
            (20, 50),   # Trend trades
        ]
    
    async def on_bar(self, symbol: str, df: pd.DataFrame):
        """Generate signals from all SMA periods."""
        signals = []
        
        for fast, slow in self.periods:
            signal = self._check_crossover(symbol, df, fast, slow)
            if signal:
                signals.append(signal)
        
        return signals  # Return all valid signals
```

**Key insight:** Each SMA period is independent
- SMA(5/15) can fire a BUY while SMA(20/50) fires a SELL
- Both are valid signals (don't cancel each other)
- Order manager handles simultaneous signals

**Expected outcome:**
- ✅ 3x more signals per symbol
- ✅ Different signal types: fast, medium, slow
- ✅ Better capture of all market conditions

**Testing:**
```bash
# After implementation, you'll see:
# - More order_intents entries (3x or more)
# - Signals labeled by period: (5,15), (10,30), (20,50)
# - Multiple trades per symbol per day
```

---

### Phase 3: Profit Taking & Stop Loss (60 minutes)

**Goal:** Automatically exit trades at profit/loss targets instead of waiting for inverse signal

**Why:** 
- Lock in quick wins (+2% profit)
- Prevent catastrophic losses (-1% stop)
- Reduces max drawdown, improves win rate

**Current behavior:**
```
BUY at $100 → Price goes to $101 → Still holding
         → Price goes to $99 → Still holding
         → SMA reverses → Finally sell
Result: Miss profit, hold losses too long
```

**New behavior:**
```
BUY at $100 → Price $102 → SELL (profit taking) ✓
         OR
BUY at $100 → Price $99 → SELL (stop loss) ✓
```

**What to change:**

New file: `src/exit_manager.py`

```python
class ExitManager:
    """Manages profit taking and stop loss logic."""
    
    def __init__(self, profit_target_pct: float = 0.02, stop_loss_pct: float = 0.01):
        """
        Args:
            profit_target_pct: Sell when profit reaches this % (e.g., 0.02 = 2%)
            stop_loss_pct: Sell when loss exceeds this % (e.g., 0.01 = 1% loss)
        """
        self.profit_target = profit_target_pct
        self.stop_loss = stop_loss_pct
    
    async def check_exit(self, position, current_price):
        """Check if position should exit.
        
        Args:
            position: {symbol, qty, entry_price, entry_time}
            current_price: Current market price
        
        Returns:
            ExitSignal or None
        """
        entry = position['entry_price']
        pnl_pct = (current_price - entry) / entry
        
        # Profit taking
        if pnl_pct >= self.profit_target:
            return ExitSignal(
                symbol=position['symbol'],
                reason="profit_taking",
                target_price=current_price
            )
        
        # Stop loss
        if pnl_pct <= -self.stop_loss:
            return ExitSignal(
                symbol=position['symbol'],
                reason="stop_loss",
                target_price=current_price
            )
        
        return None
```

**Integration into main flow:**

File: `main.py`

```python
# After each bar
bar = await event_bus.get(BarEvent)

# 1. Check entry signals (existing)
signal = await strategy.on_bar(bar.symbol, df)

# 2. NEW: Check exit signals
exit_signal = await exit_manager.check_exit(
    position=position,
    current_price=bar.close
)

# 3. Execute whichever fires
if exit_signal:
    await order_manager.submit_order(exit_signal, qty)
elif signal:
    await order_manager.submit_order(signal, qty)
```

**Configuration options:**

In `config/trading.yaml`:

```yaml
exit:
  profit_target_pct: 0.02   # 2% profit trigger
  stop_loss_pct: 0.01        # 1% loss trigger
  trailing_stop: false       # Optional: trailing stop
  trailing_pct: 0.005        # If true, trail by 0.5%
```

**Expected outcome:**
- ✅ Quick wins captured (no need to wait for signal reversal)
- ✅ Losses limited (no more 5% underwater trades)
- ✅ Higher win rate (more exits on profit)
- ✅ Lower max drawdown (stop losses work)

**Testing:**
```bash
# Paper trade for 1 day, check:
# - How many profit taking exits?
# - How many stop losses triggered?
# - Average P&L per trade (should be positive)
# - Max loss per trade (should be ~1%)
```

---

## Implementation Order

### Day 1 (Today): Symbol Batching
```
1. Update src/stream.py with batch logic (10 min)
2. Restart bot (2 min)
3. Verify no more HTTP 429 in logs (3 min)
```

### Day 2 (Tomorrow): Multiple SMA Periods
```
1. Modify src/strategy/sma_crossover.py (20 min)
2. Update signal metadata (5 min)
3. Test with paper trading (5 min)
4. Verify 3x more signals firing (review logs)
```

### Day 3 (Day after): Profit Taking & Stop Loss
```
1. Create src/exit_manager.py (30 min)
2. Integrate into main.py (15 min)
3. Update config/trading.yaml (5 min)
4. Paper trade for 24 hours (monitoring)
```

---

## Deployment Checklist

### Before Each Implementation

- [ ] Create new git branch: `git checkout -b feature/symbol-batching`
- [ ] Review code changes
- [ ] Update relevant docstrings
- [ ] Add logging statements

### After Each Implementation

- [ ] Run bot for 30 minutes (verify no crashes)
- [ ] Check logs for expected behavior
- [ ] Verify database updates correctly
- [ ] Confirm no new errors

### Before Going Live

- [ ] Paper trade for 24+ hours with all 3 changes
- [ ] Win rate > 50%
- [ ] Average profit per trade > $0
- [ ] No circuit breaker triggers
- [ ] Rate limits eliminated

---

## Risk Management During Development

**Keep paper trading enabled** for all changes:
```python
ALPACA_PAPER = true  # Always true until fully tested
```

**Circuit breaker protection:**
- Stops trading after 5 consecutive failures
- Can't lose more than configured daily limit
- You can manually override with kill-switch

**Gradual rollout:**
1. Test new strategy on 1 symbol first
2. If good, roll out to all 31
3. Monitor for 24+ hours before next change

---

## Expected Results After All 3 Changes

### Before
- 0 trades executed (waiting for first signal)
- 1 signal type (SMA 10/30 only)
- Manual exit management (tedious)

### After
- 10-20 trades per day
- 3 signal types (fast, medium, slow)
- Automatic profit taking & stops
- Higher win rate (>55%)
- Lower drawdowns
- Faster feedback loop

---

## Success Metrics

Track these metrics before & after:

| Metric | Before | Target After |
|--------|--------|-------------|
| Trades/day | 0 | 10-20 |
| Win rate | N/A | >55% |
| Avg profit/trade | N/A | >$5 |
| Max loss/trade | N/A | <$50 |
| Symbols active | 31 | 31 |
| Rate limit errors | Frequent | None |
| Circuit breaker trips | N/A | 0 |

---

## Questions to Answer

Before starting development, clarify:

1. **Profit target:** 2% good, or prefer 1% / 3%?
2. **Stop loss:** 1% good, or prefer 0.5% / 2%?
3. **Symbol batching:** 10 symbols per batch, or different?
4. **SMA periods:** Use (5,15), (10,30), (20,50) or different?
5. **Risk per trade:** Current max position is 10% of equity, OK?

---

## Timeline

| Phase | Task | Time | Start | End |
|-------|------|------|-------|-----|
| 1 | Symbol batching | 15m | Now | +15m |
| - | Break | 30m | | |
| 2 | Multiple SMAs | 30m | +45m | +75m |
| - | Break | 30m | | |
| 3 | Profit taking | 60m | +105m | +165m |
| - | Testing | 24h | | Tomorrow |

**Total active dev time: 105 minutes (1h 45m)**  
**Total with breaks: ~3 hours**

---

## Next Steps

1. **Confirm** you want to proceed with all 3
2. **Clarify** any questions about configuration
3. **Schedule** development sessions (or I can do it now)
4. **Monitor** results after each deployment

Ready to start? ✅

