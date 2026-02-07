# Orchestration Strategy: From Paper Trading to Production-Grade System

## High-Level Assessment

### Overall System Maturity

**Current State: Solid Foundation (7/10)**

✅ **Strengths:**
- Clean event-driven architecture (testable, observable)
- 3-tier safety system with hard stops
- 100% test coverage on critical paths
- Deterministic order IDs (reproducible, auditable)
- Fast restart recovery (SQLite persistence)
- Production-grade error handling (circuit breaker, reconciliation)

⚠️ **Maturity Gaps:**
- Single strategy (SMA only) = no regime awareness
- Execution quality blind spots (slippage impact unknown)
- No position-sizing algorithm (fixed qty per trade)
- Limited observability (what actually works vs hypothesis)
- No live/paper trading toggle readiness
- Missing state restoration on crash (signal history, daily limits)

### Biggest Current Risks (Priority Order)

1. **Strategy Quality Unknown** — Is SMA(10/30) actually profitable? Win rate target (55%) untested.
2. **Execution Leakage** — Market orders on spreads <0.5% seem safe, but no slippage analysis. Real impact unknown.
3. **Position Sizing Static** — Buy 10 shares always = wrong sizing for volatile vs stable symbols. NVDA 10sh ≠ GLD 10sh risk.
4. **Regime Blindness** — SMA crossovers work in trending markets, fail in ranging markets. No detection or adaptation.
5. **State Crash Loss** — Daily limits, signal history, circuit breaker count lost on restart (only bars persist).
6. **Live Capital Readiness** — Dual-gate protection exists, but switchover untested. Gap between paper and live behavior unknown.

### Readiness for Next Phase

**Paper Trading:** Ready ✅  
**Live Capital:** Not ready ⚠️ (2-3 Wins needed before safe)

**Critical path to go-live:**
1. Validate strategy is profitable (Win #2: multiple timeframes)
2. Prove execution quality (Win #4: slippage analysis)
3. Harden state recovery (Win #3: persist daily limits + signal state)
4. Test live/paper switchover (Win #5: dual-gate validation)

---

## Top Improvement Themes

### 1. Strategy: Avoid Overtrading, Improve Signal Quality

**Current state:** Single SMA(10/30) on all 31 symbols = high signal frequency, unknown quality.

**Problems:**
- No regime detection (trending vs ranging)
- All symbols treated identically (NVDA volatility ≠ GLD stability)
- No signal confirmation (crosses without volume/context = noise)
- High false-positive rate expected (SMA whipsaws)

**Direction:**
- Add regime awareness (trending vs ranging detection)
- Add confirmation filters (volume, momentum, time-of-day)
- Accept lower frequency, higher quality signals
- Segment by volatility (smaller stops for stable, larger for volatile)

### 2. Risk: Move from Fixed Qty to Dynamic Sizing

**Current state:** Buy 10 shares always (fixed across all 31 symbols).

**Problems:**
- NVDA 10 shares = volatile, large risk
- GLD 10 shares = stable, tiny risk
- No correlation with Kelly Criterion or volatility
- Can't optimize capital deployment

**Direction:**
- Size by recent volatility (ATR-based)
- Respect account equity % consistently
- Reserve capital for larger moves
- Enable better drawdown protection

### 3. Execution: Understand Real Slippage & Fees

**Current state:** Assumes 0.5% spread is only cost; no measurement of actual fills.

**Problems:**
- Market orders during earnings could be worse
- Bid-ask slippage not tracked
- Alpaca rebates/fees not accounted
- Can't optimize execution timing

**Direction:**
- Log actual fill price vs mid-quote
- Calculate realized slippage per trade
- Measure impact of time-of-day (first/last 5 min avoidance working?)
- Adjust forecast assumptions based on real data

### 4. Reliability / Ops: Harden State Persistence

**Current state:** Bars persist, but daily limits + signal state lost on restart.

**Problems:**
- Circuit breaker count resets on crash (could double-trade during recovery)
- Daily loss limit resets at restart (unsafe if crash near daily boundary)
- Last signal per symbol only in memory (possible duplicate signals after crash)
- Recovery untested

**Direction:**
- Persist circuit breaker state + timestamp
- Persist daily P&L snapshot + date
- Persist last signal per symbol to SQLite
- Test crash → recovery flow explicitly

### 5. Observability / Testing: Add Live Metrics & Backtesting

**Current state:** Logs exist but no dashboards. No way to validate strategy off-market.

**Problems:**
- Can't see signal quality (win rate, avg trade size, drawdown curve)
- Can't validate parameters without running live
- No comparison between paper and live (blind on dual-gate impact)
- No backtesting framework for new ideas

**Direction:**
- Add real-time metrics export (win rate, slippage, daily P&L)
- Build lightweight backtesting harness (replay bars from SQLite)
- Create daily/weekly performance summaries
- Compare paper vs live side-by-side (once live)

---

## Proposed Wins Roadmap

### Win #2: Multi-Timeframe SMA + Regime Detection (45 min)

**Goal:** Reduce signal noise, improve win rate, add market awareness.

**Description:**

Add three independent SMA strategies + trending/ranging regime detection:

```python
# Strategies
sma_fast = SMA(5, 15)    # Quick scalp trades
sma_medium = SMA(10, 30)  # Current (medium-term)
sma_slow = SMA(20, 50)    # Trend trades

# Regime detection
trend_score = (close - SMA(50)) / ATR(14)
is_trending = abs(trend_score) > 1.5
is_ranging = close between [SMA(20) - 2*ATR, SMA(20) + 2*ATR]

# Filter logic
if is_trending:
    use sma_slow (high confidence)
else if is_ranging:
    skip trading (avoid whipsaws)
else:
    use sma_fast or sma_medium
```

**Changes:**
- Enhance `strategy/sma_crossover.py` to support multiple periods
- Add `regime_detector.py` (trending/ranging/unknown)
- Add signal metadata (confidence, regime, period)
- Update risk manager to use signal confidence
- Add tests for regime detection (5 new tests)

**Why it matters:**
- Reduces false signals (~30% better win rate expected)
- Prevents trading in choppy markets (key risk)
- Segments signal quality (slow SMA = higher confidence)
- Demonstrates strategy sophistication (important for live capital)

**Complexity:** Medium (logic is simple, integration requires care)

**Success metric:** Win rate >55%, fewer consecutive losses, equity curve smoother

---

### Win #3: State Persistence + Deterministic Recovery (40 min)

**Goal:** Ensure consistent state across crashes, eliminate recovery race conditions.

**Description:**

Persist all runtime state that resets on restart:

```python
# Add to bot_state table
circuit_breaker_failures: int (incremented, persists across restart)
circuit_breaker_since: timestamp (when last failure occurred)
daily_pnl: float (sum of closed trades today)
daily_trade_count: int (count of orders submitted)
last_signal: {symbol: signal_type} (prevent duplicates)
session_start: timestamp (for daily limits)

# On startup
1. Load bot_state
2. Check if session_start is TODAY
3. If not, reset daily limits (new trading day)
4. Restore circuit breaker count
5. Load last signal per symbol
6. Resume trading safely
```

**Changes:**
- Extend SQLite schema (add bot_state columns)
- Enhance `state_store.py` to persist runtime state
- Add `recovery.py` (startup sequence with state restoration)
- Add crash recovery tests (simulate crash → restart)

**Why it matters:**
- **Safety:** Daily limits actually enforced across restarts
- **Consistency:** No double-trading during recovery
- **Auditability:** Full state history in database
- **Trust:** Live capital requires deterministic recovery

**Complexity:** Low (straightforward state management)

**Success metric:** No state resets on restart, daily limits hold, circuit breaker persists

---

### Win #4: Execution Quality Analysis (35 min)

**Goal:** Measure real execution costs, optimize for slippage.

**Description:**

Add slippage tracking and analysis:

```python
# In order_manager.py, after fill received
fill_price = order.filled_avg_price
mid_price = (bid + ask) / 2  # from snapshot
slippage = abs(fill_price - mid_price)
slippage_pct = (slippage / mid_price) * 100

# Store metrics
INSERT INTO execution_metrics (
    order_id, symbol, side, slippage_pct, 
    bid_ask_spread, time_to_fill, hour_of_day
)

# Analyze
avg_slippage = SELECT AVG(slippage_pct) FROM execution_metrics
time_of_day_impact = SELECT hour, AVG(slippage) GROUP BY hour
symbol_impact = SELECT symbol, AVG(slippage) GROUP BY symbol
```

**Changes:**
- Add `execution_metrics` table to SQLite
- Capture mid-quote at order time (snapshot)
- Log fill price vs mid-quote delta
- Daily slippage report (by symbol, time-of-day, market condition)

**Why it matters:**
- **Reality check:** See if 0.5% spread filter is actually working
- **Optimization:** Identify best times/symbols for trading
- **Expectations:** Adjust forecast models with real data
- **Live prep:** Know expected friction costs before going live

**Complexity:** Low (mostly logging + reporting)

**Success metric:** Avg slippage <0.1%, identify time-of-day patterns

---

### Win #5: Dynamic Position Sizing (50 min)

**Goal:** Right-size trades by volatility, respect Kelly Criterion targets.

**Description:**

Replace fixed "buy 10 shares" with volatility-adjusted sizing:

```python
# For each symbol
atr = ATR(14)  # Average True Range (volatility)
recent_vol = std(returns, 20)

# Target: risk 1% of account per trade
account_equity = $100,000
target_risk = 0.01 * account_equity  # $1,000

# Position size formula
qty = target_risk / (stop_distance * price)
qty = max(qty, 1)  # min 1 share
qty = min(qty, max_position_size)  # max 10% cap

# Example:
# NVDA $400, stop at -$5 (volatile) → small qty
# GLD $180, stop at -$1.80 (stable) → larger qty
```

**Changes:**
- Add `position_sizer.py` (Kelly-based sizing)
- Calculate stop distance from ATR or recent moves
- Integrate into order manager before submission
- Add sizing tests (verify respects limits)

**Why it matters:**
- **Risk consistency:** Same % risk per trade regardless of symbol
- **Capital efficiency:** Deploy more capital to stable trades
- **Volatility adaptation:** Dynamic, not static
- **Professional:** Standard practice in institutional trading

**Complexity:** Medium (math is simple, testing requires care)

**Success metric:** All trades risk ~1% of equity, capital utilization >60%

---

### Win #6: Backtesting Framework (60 min)

**Goal:** Validate strategy parameters and new ideas before paper trading.

**Description:**

Build lightweight backtesting using existing SQLite bars:

```python
# backtest.py
class Backtester:
    def __init__(self, symbols, start_date, end_date):
        self.bars = load_from_sqlite(symbols, start_date, end_date)
    
    def run(self, strategy_params):
        equity = 100000
        trades = []
        
        for bar in self.bars:
            signal = self.strategy.on_bar(bar)
            if signal:
                order = self.risk_mgr.check(signal)
                if order:
                    trades.append(execute_backtest(order, bar))
                    equity += trade.pnl
        
        return {
            'total_return': (equity - 100000) / 100000,
            'win_rate': sum(1 for t in trades if t.pnl > 0) / len(trades),
            'max_drawdown': calculate_drawdown(equity_curve),
            'trades': trades
        }
```

**Changes:**
- Add `backtest.py` module
- Add `backtest_runner.py` (CLI for running tests)
- Export results (JSON report + equity curve)
- Add backtest validation tests

**Why it matters:**
- **Parameter tuning:** Test SMA(5,15) vs (7,21) vs (10,30) objectively
- **Before paper:** Validate ideas work before going live
- **Debugging:** Understand why specific trades fail
- **Live confidence:** Data-backed parameters, not guesses

**Complexity:** Medium (straightforward but requires care on edge cases)

**Success metric:** Can backtest 6 months of data in <5 seconds, report matches live reality

---

### Win #7: Dual-Gate Live/Paper Toggle + Switchover Test (30 min)

**Goal:** Validate live trading mode works before real capital.

**Description:**

Add explicit dual-gate toggle with validation:

```python
# config/trading.yaml
mode: paper  # or "live"

# In broker.py
if mode == "live":
    assert ALLOW_LIVE_TRADING == True  # Dual gate #1
    assert ALPACA_PAPER == False        # Dual gate #2
    assert account_equity < 50000       # Start small
    assert position_size < 100          # Limit per trade
    logger.warning("LIVE MODE ENABLED - REAL CAPITAL AT RISK")

# In main.py startup
if mode == "live":
    print("Ready to start live trading? (yes/no)")
    if input() != "yes":
        exit()
```

**Changes:**
- Add live/paper toggle to config
- Add dual-gate validation in broker
- Add startup confirmation (explicit)
- Add "test switchover" mode (live config, paper account)

**Why it matters:**
- **Safety:** Can't accidentally go live
- **Confidence:** Tested switchover before real capital
- **Auditability:** Clear log of when/how live mode started
- **Recovery:** Can quickly revert to paper if needed

**Complexity:** Low (mostly configuration + validation)

**Success metric:** Can toggle paper↔live safely, switchover test passes

---

## Red Flags: "Do Not Do Yet"

### ❌ Do NOT: Add ML/Deep Learning
**Why:** SMA strategy not yet validated as profitable. ML before baseline is cargo cult.  
**When:** After 8+ weeks live data showing consistent +15% annual return.

### ❌ Do NOT: Add Portfolio Hedging
**Why:** Single account, 31 symbols enough diversification for now.  
**When:** After managing >$500k or holding leveraged positions.

### ❌ Do NOT: Add Multi-Broker Support
**Why:** Alpaca integration not yet bulletproof. Focus on one.  
**When:** After 6+ months live, >$100k AUM proven.

### ❌ Do NOT: Add Margin/Leverage
**Why:** No cushion if strategy fails. Paper trading capital at risk first.  
**When:** After 12+ months live, 60%+ win rate, <10% max drawdown.

### ❌ Do NOT: Refactor Architecture
**Why:** Current event-driven design is solid. Incremental wins are safer.  
**When:** Only if new requirements can't fit (unlikely for 2 years).

### ❌ Do NOT: Add Real-Time Dashboard
**Why:** Dashboards are expensive, logs + reports are enough initially.  
**When:** After proving strategy, reducing operational burden.

### ❌ Do NOT: Automate Daily Parameter Tuning
**Why:** "Overfitting to yesterday" kills live returns. Manual quarterly review is safer.  
**When:** After 12+ months live data validates robustness.

---

## Implementation Sequence (Critical Dependencies)

```
NOW: Win #1 (Symbol Batching) ✅ DONE

Week 1:
  → Win #2 (Multi-timeframe SMA + regime)
  → Win #3 (State persistence)

Week 2:
  → Win #4 (Execution quality analysis)
  → Win #5 (Dynamic position sizing)

Week 3-4:
  → Win #6 (Backtesting)
  → Win #7 (Live/paper toggle)

Week 5+:
  → Paper trading validation (2+ weeks)
  → Live capital prep
  → Go live (if profitable)
```

---

## Success Criteria: Road to Live Capital

**Before Live:**
- ✅ Win #2-#7 deployed + tested
- ✅ 4+ weeks paper trading data
- ✅ Win rate >55%
- ✅ Max drawdown <10%
- ✅ Avg slippage <0.15%
- ✅ Dual-gate tested + verified
- ✅ Daily limits working
- ✅ State recovery verified

**First Live Trade:**
- Start with 100 shares max per trade
- Max $5k AUM (0.5% of eventual size)
- Trade only 8 symbols (reduce surface area)
- Monitor 24/7 for first week

**Scale to Full:**
- After 2 weeks live profitable: double size
- After 1 month live: enable all 31 symbols
- After 3 months live: scale to $50k AUM

---

## Summary Table

| Win | Name | Time | Impact | Risk | When |
|-----|------|------|--------|------|------|
| #2 | Multi-timeframe SMA | 45m | Signal quality +30% | Low | Now |
| #3 | State persistence | 40m | Crash safety | Low | After #2 |
| #4 | Execution analysis | 35m | Slippage visibility | Low | After #2 |
| #5 | Dynamic sizing | 50m | Capital optimization | Med | After #3 |
| #6 | Backtesting | 60m | Parameter validation | Low | After #2 |
| #7 | Live/paper toggle | 30m | Go-live safety | Low | After #5 |

---

## Final Assessment

**Current system is production-grade for paper trading.** You can safely run paper validation for months.

**Gap to live capital is 2-3 Wins:** Multi-timeframe (quality), state persistence (safety), dynamic sizing (efficiency).

**Conservative path:** Complete Wins #2-#5 before live. Wins #6-#7 validate before switching capital.

**Risk-adjusted recommendation:** 
1. Deploy Win #2 this week (highest ROI on signal quality)
2. Deploy Win #3 immediately after (crash safety non-negotiable)
3. Run 4+ weeks paper with #2+#3 to validate win rate
4. Deploy Win #5 (sizing) before live
5. Do full backtest + dual-gate test before real capital

**Timeline to live capital: 5-6 weeks (realistic, safe).**

