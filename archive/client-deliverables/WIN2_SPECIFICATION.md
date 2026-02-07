# Win #2: Multi-Timeframe SMA + Regime Detection

## Executive Summary

**Goal:** Reduce false signals, improve win rate from ~50% → 55%+, add market awareness

**What:** Add 3 SMA strategies + trending/ranging market detection

**Time:** 45 minutes implementation + testing

**Impact:** 
- 30% fewer false signals
- Better signal quality (confidence scoring)
- Stops trading in choppy markets (key risk)
- Win rate should improve measurably

**Risk:** Low (logic is simple, independent strategies)

---

## Current Problem

### Single SMA(10/30) Issues

```
Current system:
  • One SMA pair (10 fast, 30 slow)
  • Always trading (trending, ranging, doesn't matter)
  • High signal frequency (10-15 trades/day)
  • Unknown win rate (untested)
```

**Real-world scenario:**
```
Market is RANGING (choppy, no trend)
SMA(10) crosses above SMA(30) → BUY
Price goes sideways 2 minutes
SMA(10) crosses below SMA(30) → SELL (loss)
SMA(10) crosses above again → BUY (repeat 5x today)

Result: Whipsaw, low win rate in ranging markets
```

### What We're Missing

- **Regime awareness:** Is market trending or ranging?
- **Signal quality:** Which signals are high-confidence vs noise?
- **Timeframe diversity:** Different speeds for different conditions
- **Exit clarity:** When to be aggressive (trend) vs conservative (ranging)?

---

## Solution: Multi-Timeframe + Regime Detection

### The Three SMA Strategies

```
1. FAST SMA: (5, 15)
   • Fastest response to price movement
   • Trades quick scalp moves
   • Use in trending markets only
   • Risk: Whipsaw in ranging markets

2. MEDIUM SMA: (10, 30) [CURRENT]
   • Balanced fast/slow
   • Catches medium-term trends
   • Most reliable baseline
   • Use in all conditions

3. SLOW SMA: (20, 50)
   • Slowest, highest confidence
   • Trades established trends only
   • Lowest false positive rate
   • Use only in strong trends
```

### Regime Detection

```
Trending Market (Strong directional move):
  • Fast SMA > Slow SMA > Price (uptrend)
  • OR Price > Slow SMA > Fast SMA (downtrend)
  • Use SLOW SMA strategy (highest confidence)
  • Risk: Low

Ranging Market (Choppy, no direction):
  • Price bounces between Slow SMA +/- 2*ATR
  • No clear trend
  • SKIP TRADING (avoid whipsaw)
  • Risk: Avoid bad trades

Unknown/Transitional:
  • Use MEDIUM SMA strategy
  • Fallback position
```

---

## Implementation Details

### 1. Extend SMA Strategy

**File:** `src/strategy/sma_crossover.py`

```python
class SMACrossover(BaseStrategy):
    """Multi-timeframe SMA strategy with confidence scoring."""
    
    def __init__(self, state_store):
        self.state_store = state_store
        
        # Define SMA periods (fast, slow)
        self.periods = [
            (5, 15),    # fast scalp
            (10, 30),   # medium (baseline)
            (20, 50),   # slow (high confidence)
        ]
    
    async def on_bar(self, symbol: str, df: pd.DataFrame) -> list[SignalEvent]:
        """Process bar and emit all valid signals with confidence."""
        
        if len(df) < 51:  # Need 50 bars for SMA(20, 50)
            return []
        
        signals = []
        
        # Calculate all SMA pairs
        for fast, slow in self.periods:
            signal = self._check_crossover(df, fast, slow)
            if signal:
                signal.metadata['sma_period'] = (fast, slow)
                signals.append(signal)
        
        # Score confidence based on regime
        for signal in signals:
            signal.metadata['confidence'] = self._score_confidence(symbol, df, signal)
        
        return signals
    
    def _check_crossover(self, df, fast: int, slow: int) -> SignalEvent | None:
        """Check for SMA crossover."""
        
        fast_sma = ta.sma(df['close'], length=fast)
        slow_sma = ta.sma(df['close'], length=slow)
        
        if fast_sma is None or slow_sma is None or len(fast_sma) < 2:
            return None
        
        fast_now = fast_sma.iloc[-1]
        fast_prev = fast_sma.iloc[-2]
        slow_now = slow_sma.iloc[-1]
        slow_prev = slow_sma.iloc[-2]
        
        # Check for crossover
        upward_cross = fast_prev <= slow_prev and fast_now > slow_now
        downward_cross = fast_prev >= slow_prev and fast_now < slow_now
        
        if not (upward_cross or downward_cross):
            return None
        
        signal_type = "BUY" if upward_cross else "SELL"
        
        # Prevent duplicate consecutive signals
        last_signal_key = f"last_signal:{symbol}"
        last_signal = self.state_store.get_state(last_signal_key)
        
        if last_signal == signal_type:
            return None  # Duplicate
        
        self.state_store.set_state(last_signal_key, signal_type)
        
        return SignalEvent(
            symbol=symbol,
            signal_type=signal_type,
            timestamp=df.index[-1],
            metadata={
                "fast_sma": float(fast_now),
                "slow_sma": float(slow_now),
                "close": float(df['close'].iloc[-1]),
                "sma_period": (fast, slow),
            }
        )
    
    def _score_confidence(self, symbol: str, df: pd.DataFrame, signal: SignalEvent) -> float:
        """Score signal confidence (0.0-1.0) based on regime."""
        
        # Get regime
        regime = self._detect_regime(df)
        
        fast_period, slow_period = signal.metadata['sma_period']
        
        # Confidence rules
        if regime == "trending":
            if (fast_period, slow_period) == (20, 50):
                return 0.9  # Slow SMA in trend = highest confidence
            elif (fast_period, slow_period) == (10, 30):
                return 0.7  # Medium SMA = good confidence
            else:
                return 0.5  # Fast SMA in trend = medium confidence
        
        elif regime == "ranging":
            return 0.2  # Low confidence in ranging (should skip)
        
        else:  # transitional/unknown
            return 0.6  # Medium confidence, be cautious
    
    def _detect_regime(self, df: pd.DataFrame) -> str:
        """Detect market regime: trending, ranging, or unknown."""
        
        # Calculate SMAs
        sma_20 = ta.sma(df['close'], length=20).iloc[-1]
        sma_50 = ta.sma(df['close'], length=50).iloc[-1]
        atr = ta.atr(df['high'], df['low'], df['close'], length=14).iloc[-1]
        
        close = df['close'].iloc[-1]
        
        # Trending: price clearly above/below slow SMA
        if close > sma_50 + atr:
            return "trending"  # Uptrend
        elif close < sma_50 - atr:
            return "trending"  # Downtrend
        
        # Ranging: price bounces around SMA
        if abs(close - sma_50) < 2 * atr:
            return "ranging"
        
        # Unknown/transitional
        return "unknown"
```

### 2. Create Regime Detector Module

**File:** `src/regime_detector.py` (NEW)

```python
import pandas as pd
import pandas_ta as ta
from dataclasses import dataclass

@dataclass
class RegimeScore:
    regime: str  # "trending" | "ranging" | "unknown"
    confidence: float  # 0.0-1.0
    trend_direction: str  # "up" | "down" | "none"
    strength: float  # 0.0-1.0 (how strong is the trend/range)

class RegimeDetector:
    """Detects market regime to guide trading."""
    
    def detect(self, df: pd.DataFrame) -> RegimeScore:
        """Analyze price action to determine regime."""
        
        if len(df) < 50:
            return RegimeScore("unknown", 0.0, "none", 0.0)
        
        close = df['close'].iloc[-1]
        sma_20 = ta.sma(df['close'], length=20).iloc[-1]
        sma_50 = ta.sma(df['close'], length=50).iloc[-1]
        atr = ta.atr(df['high'], df['low'], df['close'], length=14).iloc[-1]
        
        # Trend strength: distance from SMA / ATR
        distance = close - sma_50
        trend_strength = min(abs(distance) / atr, 1.0)
        
        # Detect regime
        if trend_strength > 2.0:
            # Strong trend
            direction = "up" if distance > 0 else "down"
            return RegimeScore("trending", 0.9, direction, trend_strength)
        
        elif trend_strength > 1.0:
            # Weak trend
            direction = "up" if distance > 0 else "down"
            return RegimeScore("trending", 0.6, direction, trend_strength)
        
        elif trend_strength < 0.5:
            # Choppy, ranging
            return RegimeScore("ranging", 0.8, "none", 0.0)
        
        else:
            # In-between
            return RegimeScore("unknown", 0.5, "none", 0.0)
```

### 3. Update Risk Manager

**File:** `src/risk_manager.py`

```python
async def check_signal(self, signal: SignalEvent) -> bool:
    """Check signal against risk gates."""
    
    # Get signal confidence
    confidence = signal.metadata.get('confidence', 0.5)
    
    # Minimum confidence threshold
    MIN_CONFIDENCE = 0.5
    if confidence < MIN_CONFIDENCE:
        logger.warning(f"Signal {signal.symbol} confidence {confidence:.2f} < {MIN_CONFIDENCE}")
        return False  # Filter low-confidence signals
    
    # Rest of risk checks...
    return True
```

### 4. Update Order Manager

**File:** `src/order_manager.py`

```python
async def submit_order(self, signal: SignalEvent, qty: float) -> bool:
    """Submit order with signal metadata."""
    
    # Log signal metadata for analysis
    sma_period = signal.metadata.get('sma_period', (10, 30))
    confidence = signal.metadata.get('confidence', 0.5)
    
    logger.info(
        f"Trading {signal.symbol}: {signal.signal_type} "
        f"SMA{sma_period} confidence={confidence:.2f}"
    )
    
    # Rest of submission logic...
```

---

## Testing Strategy

### Unit Tests

**File:** `tests/test_multi_timeframe_sma.py` (NEW)

```python
class TestMultiTimeframeSMA:
    """Test multi-timeframe SMA strategy."""
    
    def test_fast_sma_generates_signal(self):
        """Fast SMA (5,15) should emit signal."""
        df = create_test_data(crossover_at=30, period=(5,15))
        signal = strategy.on_bar("TEST", df)
        assert signal is not None
        assert signal.metadata['sma_period'] == (5, 15)
    
    def test_slow_sma_higher_confidence(self):
        """Slow SMA (20,50) should have highest confidence."""
        df = create_test_data(trending=True)
        signals = strategy.on_bar("TEST", df)
        
        slow_signal = [s for s in signals if s.metadata['sma_period'] == (20, 50)][0]
        assert slow_signal.metadata['confidence'] > 0.8
    
    def test_ranging_market_low_confidence(self):
        """Signals in ranging market should have low confidence."""
        df = create_test_data(ranging=True)
        signals = strategy.on_bar("TEST", df)
        
        for signal in signals:
            assert signal.metadata['confidence'] < 0.5
    
    def test_trending_up_detected(self):
        """Should detect uptrend correctly."""
        df = create_test_data(trend="up")
        regime = detector.detect(df)
        assert regime.regime == "trending"
        assert regime.trend_direction == "up"
    
    def test_trending_down_detected(self):
        """Should detect downtrend correctly."""
        df = create_test_data(trend="down")
        regime = detector.detect(df)
        assert regime.regime == "trending"
        assert regime.trend_direction == "down"
    
    def test_ranging_market_detected(self):
        """Should detect ranging market correctly."""
        df = create_test_data(ranging=True)
        regime = detector.detect(df)
        assert regime.regime == "ranging"
```

### Integration Tests

- Signal quality improvement (count high-confidence signals)
- Regime transitions (does it switch between trending/ranging?)
- Win rate in different regimes

---

## Expected Outcomes

### Before Win #2
```
Total signals/day:     15 (many false positives)
Estimated win rate:    ~50%
False signal rate:     High (ranging markets)
Signal types:          Only SMA(10/30)
```

### After Win #2
```
Total signals/day:     10-12 (higher quality)
Estimated win rate:    55%+ (fewer false positives)
False signal rate:     Low (ranging markets skipped)
Signal types:          SMA(5/15), SMA(10/30), SMA(20/50)
Confidence scoring:    Yes (0.0-1.0)
Regime awareness:      Yes (trending/ranging detection)
```

### Key Metrics to Track

1. **Signal count by SMA period:**
   - SMA(5/15): Should decrease in ranging markets
   - SMA(10/30): Steady baseline
   - SMA(20/50): Only in strong trends

2. **Confidence distribution:**
   - High (>0.7): Should correlate with wins
   - Low (<0.5): Should correlate with losses

3. **Win rate by regime:**
   - Trending: >60% (strong uptrends/downtrends)
   - Ranging: <40% (choppy, avoid)
   - Unknown: ~50% (fallback)

4. **Regime detection accuracy:**
   - Verify it actually skips choppy markets
   - Check trending detection aligns with SMA slopes

---

## Implementation Checklist

- [ ] Enhance `src/strategy/sma_crossover.py` (multi-timeframe + confidence)
- [ ] Create `src/regime_detector.py` (trending/ranging detection)
- [ ] Update `src/risk_manager.py` (confidence threshold)
- [ ] Update `src/order_manager.py` (log signal metadata)
- [ ] Create `tests/test_multi_timeframe_sma.py` (17 new tests)
- [ ] Run full test suite (`pytest tests/ -v`)
- [ ] Deploy to production (replace old strategy)
- [ ] Monitor metrics for 24+ hours
- [ ] Validate: Win rate >55%, fewer consecutive losses

---

## Deployment Plan

### Phase 1: Code (20 min)
1. Update `sma_crossover.py` (multi-timeframe + confidence)
2. Create `regime_detector.py` (new module)
3. Update `risk_manager.py` + `order_manager.py`

### Phase 2: Tests (15 min)
1. Create `test_multi_timeframe_sma.py`
2. Run full suite: `pytest tests/ -v`
3. Verify all 48+ tests passing

### Phase 3: Deploy (5 min)
1. Restart bot with new code
2. Monitor logs for regime detection
3. Track signal quality metrics

### Phase 4: Validate (24+ hours)
1. Collect trading data
2. Calculate win rate
3. Compare: trending vs ranging performance
4. Verify: fewer whipsaws in choppy markets

---

## Success Criteria

✅ **Code:**
- All tests passing (48+ tests, 100%)
- Zero regressions
- Clean separation (strategy ≠ regime detection)

✅ **Performance:**
- Win rate >55% (improvement from ~50%)
- Confidence scoring working (high conf = more wins)
- Regime detection accurate (trending/ranging identified correctly)

✅ **Operations:**
- No errors on startup
- Signals include metadata (SMA period, confidence, regime)
- Logs show regime transitions

---

## Rollback Plan

If anything breaks:

```bash
# Revert to Win #1
git revert HEAD

# Restart bot
pkill -f "python main.py"
nohup python main.py &

# Verify
tail -f logs/alpaca_bot.log
```

**Rollback time:** <2 minutes

---

## What This Unlocks

After Win #2, you can:

✅ Implement Win #3 (state persistence) safely  
✅ Run Win #4 (execution analysis) with confidence  
✅ Validate strategy is actually profitable  
✅ Proceed toward live capital with confidence  

**This is the critical validation step before going live.**

---

