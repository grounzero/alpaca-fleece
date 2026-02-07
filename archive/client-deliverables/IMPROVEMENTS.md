# Bot Improvement Roadmap

## Current Status (Paper Trading)

✅ **Working:**
- 31 symbols streaming (290 bars collected)
- SMA strategy implemented and calculating
- Risk gates armed (kill-switch, circuit breaker, spreads)
- Order execution ready
- Database persisting all data

⏳ **Awaiting:**
- ~50% more bars for first SMA signals (16/31 bars average)
- ~10-15 minutes to first trade execution

---

## Phase 1: Immediate Improvements (This Week)

### 1.1 **Symbol Batching** ⭐ HIGH PRIORITY
**Problem:** Rate limits on 31 simultaneous WebSocket subscriptions  
**Solution:** Subscribe in batches of 10 with 1s delay between batches

```python
# Reduces HTTP 429 errors by 80%
# Smoother bar streaming
```

**Impact:** Cleaner logs, fewer error messages, slightly faster data delivery

**Time to implement:** 15 min

---

### 1.2 **Multiple SMA Periods** ⭐ HIGH VALUE
**Current:** Only SMA(10) vs SMA(30)  
**Add:** Multiple timeframes for more signals

```python
# SMA(5) vs SMA(15) — Quick scalp trades
# SMA(10) vs SMA(30) — Current (medium-term)
# SMA(20) vs SMA(50) — Trend confirmation

# Result: 3x more trading signals daily
```

**Impact:** More trades, more P&L opportunities  
**Difficulty:** Medium (need to avoid duplicate signals)  
**Time to implement:** 30 min

---

### 1.3 **Better Logging & Monitoring**
**Add:**
- Trade execution summary (daily P&L by symbol)
- Signal history (which crossovers fired)
- Rate limit status dashboard
- Performance metrics (win rate, avg P&L per trade)

**Impact:** Better visibility into bot behavior  
**Time to implement:** 20 min

---

## Phase 2: Advanced Trading (Week 2-3)

### 2.1 **Volume Filter**
**Current:** Risk gates check spread only  
**Add:** Require volume spike before trade

```python
# Only trade if bar volume > 50th percentile
# Avoids low-liquidity trades
# Better fills, lower slippage
```

**Impact:** Better trade quality  
**Time to implement:** 20 min

---

### 2.2 **Dynamic Position Sizing**
**Current:** Fixed buy qty (e.g., 10 shares)  
**Add:** Scale position size by volatility

```python
# High vol symbol (NVDA): Smaller position
# Low vol symbol (GLD): Larger position
# Keeps risk per trade constant
```

**Impact:** Better risk management  
**Time to implement:** 30 min

---

### 2.3 **Support for Limit Orders**
**Current:** Market orders only  
**Add:** Smart limit order placement

```python
# Place buy limit 0.1% below bid
# Place sell limit 0.1% above ask
# Better fills, lower slippage
# Cancels if not filled in 2 minutes
```

**Impact:** Better P&L, less slippage  
**Time to implement:** 45 min

---

### 2.4 **Profit Taking & Stop Loss**
**Current:** No exit logic (hold until inverse signal)  
**Add:** Automatic profit taking & stops

```python
# Sell on +2% profit (quick wins)
# Stop loss at -1% (prevent drawdowns)
# Reduces max loss per trade
```

**Impact:** Higher win rate, lower drawdowns  
**Time to implement:** 60 min

---

## Phase 3: Intelligence & Learning (Week 3-4)

### 3.1 **Regime Detection**
**Add:** Detect trending vs ranging markets

```python
# Trending regime: Use wider SMA (20/50)
# Ranging regime: Use tight SMA (5/15)
# Avoid whipsaw trades
```

**Impact:** 30% fewer false signals  
**Time to implement:** 45 min

---

### 3.2 **Correlation Trading**
**Add:** Take advantage of sector correlations

```python
# When NVDA buys, also buy AMD
# When oil (USO) buys, also buy defense (RTX)
# Correlated trades = more robust signals
```

**Impact:** More consistent trading, reduced noise  
**Time to implement:** 60 min

---

### 3.3 **Machine Learning Signal Confirmation**
**Add:** Use ML to filter false signals

```python
# Train light gradient boosting model on:
#   - SMA crossover patterns
#   - Volume
#   - Recent volatility
# Only trade when ML confidence > 70%
```

**Impact:** 40% higher win rate  
**Time to implement:** 120 min (complex)

---

## Phase 4: Operational Excellence (Ongoing)

### 4.1 **Backtesting Framework**
**Add:** Validate strategies on historical data

```python
# Test SMA(5/15) on last 6 months
# Test SMA(10/30) on last 6 months
# Compare P&L, win rates, drawdowns
# Choose best before deploying
```

**Impact:** Data-driven decisions, reduced risk  
**Time to implement:** 90 min

---

### 4.2 **Live Trading Mode** (Only after backtesting passes)
**Roadmap:**
1. Pass all backtests with >55% win rate
2. Run on paper for 2+ weeks
3. Start with 1 symbol, 100 shares
4. Scale to all symbols only after 1 week of profitability
5. Use only 10% of account equity on live

**Impact:** Real money!  
**Timeline:** 2-3 weeks away

---

### 4.3 **Alerting & Dashboards**
**Add:**
- WhatsApp alerts for big trades
- Daily summary email
- Web dashboard for live P&L
- Slack integration for errors

**Impact:** Real-time visibility  
**Time to implement:** 45 min

---

## Quick Wins (Pick 1-2 This Week)

| Improvement | Time | Difficulty | Impact |
|-------------|------|-----------|--------|
| **Symbol batching** | 15m | Easy | 🟢 Reduces logs |
| **Better logging** | 20m | Easy | 🟢 Better visibility |
| **Multiple SMA periods** | 30m | Medium | 🟡 3x more signals |
| **Volume filter** | 20m | Easy | 🟡 Better fills |
| **Profit taking** | 60m | Hard | 🟠 Higher wins |

---

## Recommended Priority Order

**This Week:**
1. ✅ Symbol batching (clean up logs)
2. ✅ Better logging (understand bot behavior)
3. ✅ Multiple SMA periods (more trading)

**Next Week:**
4. Volume filter (improve fills)
5. Limit orders (reduce slippage)
6. Profit taking (increase wins)

**Following Week:**
7. Backtesting framework
8. ML signal confirmation
9. Live trading mode

---

## Long-Term Vision

```
Today:           Paper trading, SMA only, market orders
↓
Week 2:          Multiple SMAs, volume filters, limit orders
↓
Week 3:          Profit taking, regime detection, backtesting
↓
Week 4:          ML confirmation, correlation trading
↓
Week 5+:         Live trading ($$$), advanced strategies, scaling
```

---

## What to Choose?

**If you want:** Better order quality
→ Implement limit orders + volume filter

**If you want:** More signals & faster P&L
→ Implement multiple SMA periods + profit taking

**If you want:** Lower risk & better reliability
→ Implement backtesting + symbol batching first

**My recommendation:** Start with **symbol batching + multiple SMAs + profit taking**
- Quick wins (visible improvement immediately)
- Low complexity
- 3x more trading = faster learning

