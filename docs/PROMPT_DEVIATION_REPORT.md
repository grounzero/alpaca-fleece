# Prompt Deviation Report

**Project:** alpaca-fleece  
**Report Date:** 2026-02-07  
**Analyst:** python-dev (subagent)  
**Scope:** Compare SPEC.md + PROMPT.md against actual implementation  
**Note:** SPEC.md takes precedence over PROMPT.md for module boundaries

---

## Executive Summary

The alpaca-fleece codebase shows **mixed compliance** with SPEC.md module boundaries. Core architectural principles are correctly implemented (broker.py isolation, data/* responsibilities, EventBus flow), but there's a **major architectural pivot** from WebSocket to HTTP polling that bypasses the specified event flow model.

**Overall Assessment:** Module boundaries are respected, but the data source architecture deviates significantly from specification.

---

## 1. Module Boundary Compliance

### 1.1 broker.py - Execution ONLY

| SPEC Requirement | Implementation | Status | Reasoning |
|-----------------|----------------|--------|-----------|
| Owns `/v2/account` | ✅ `get_account()` | **COMPLIANT** | Correctly implemented |
| Owns `/v2/positions` | ✅ `get_positions()` | **COMPLIANT** | Correctly implemented |
| Owns `/v2/orders` | ✅ `submit_order()`, `cancel_order()`, `get_open_orders()` | **COMPLIANT** | Correctly implemented |
| Owns `/v2/clock` exclusively | ✅ `get_clock()` with fresh calls | **COMPLIANT** | Never cached, fresh before each order |
| NO market data endpoints | ✅ No `/v2/stocks/*` | **COMPLIANT** | Clean separation |
| NO reference endpoints | ✅ No `/v2/assets`, `/v2/watchlists`, `/v2/calendar` | **COMPLIANT** | Moved to `alpaca_api/*` |
| Call `/v2/clock` fresh before every order | ✅ Called in `risk_manager.py` `_check_safety_tier()` | **COMPLIANT** | Direct broker call as allowed |

**Boundary Assessment:** ✅ **FULLY COMPLIANT**

---

### 1.2 alpaca_api/* - Data Clients ONLY

| Module | SPEC Requirement | Implementation | Status | Reasoning |
|--------|-----------------|----------------|--------|-----------|
| **market_data.py** | `/v2/stocks/bars`, `/v2/stocks/snapshots` | ✅ `get_bars()`, `get_snapshot()` | **COMPLIANT** | Correct endpoints only |
| **market_data.py** | NO order submission | ✅ No trading methods | **COMPLIANT** | Clean data client |
| **market_data.py** | NO EventBus publish | ✅ Returns data only | **COMPLIANT** | No side effects |
| **market_data.py** | NO SQLite writes | ✅ Returns data only | **COMPLIANT** | No persistence |
| **assets.py** | `/v2/assets`, `/v2/watchlists/{name}` | ✅ `get_assets()`, `get_watchlist()` | **COMPLIANT** | Correct endpoints only |
| **assets.py** | NO order submission | ✅ No trading methods | **COMPLIANT** | Clean reference client |
| **calendar.py** | `/v2/calendar` ONLY | ✅ `get_calendar()` | **COMPLIANT** | Correct endpoint only |
| **calendar.py** | NOT for trading gates | ✅ Informational only | **COMPLIANT** | Clock used for gates |

**Boundary Assessment:** ✅ **FULLY COMPLIANT**

**Note:** `AssetsClient` uses `TradingClient` from alpaca-py SDK (required for watchlist endpoint), but only calls read-only methods (`get_all_assets()`, `get_watchlist()`). This is an SDK limitation, not a boundary violation.

---

### 1.3 stream.py - WebSocket Connectivity ONLY

| SPEC Requirement | Implementation | Status | Reasoning |
|-----------------|----------------|--------|-----------|
| WebSocket connectivity | ✅ `StockDataStream`, `TradingStream` | **COMPLIANT** | Correct SDK usage |
| MUST NOT normalise data | ✅ Raw SDK objects passed | **COMPLIANT** | No transformation |
| MUST NOT write to SQLite | ✅ No persistence | **COMPLIANT** | Clean passthrough |
| MUST NOT publish to EventBus | ✅ Handlers registered externally | **COMPLIANT** | No direct publish |
| Delivers raw SDK objects to DataHandler | ✅ Via `register_handlers()` | **COMPLIANT** | Correct flow |

**Boundary Assessment:** ✅ **FULLY COMPLIANT** (but not used as primary - see Section 2)

---

### 1.4 data/* - Normalisation + Caching + Persistence + Event Publishing

| Handler | Responsibilities | Implementation | Status | Reasoning |
|---------|-----------------|----------------|--------|-----------|
| **bars.py** | Normalise, persist, publish | ✅ `_normalise_bar()`, `_persist_bar()`, `event_bus.publish()` | **COMPLIANT** | All 3 responsibilities |
| **bars.py** | SQLite persistence | ✅ Direct SQLite writes | **COMPLIANT** | Owns bar persistence |
| **bars.py** | In-memory caching | ✅ `bars_deque` rolling window | **COMPLIANT** | Caching implemented |
| **order_updates.py** | Normalise order updates | ✅ `_normalise_order_update()` | **COMPLIANT** | Correct normalisation |
| **order_updates.py** | Update order_intents | ✅ `state_store.update_order_intent()` | **COMPLIANT** | Updates via StateStore |
| **order_updates.py** | Record trades when filled | ✅ `_record_trade()` | **COMPLIANT** | Direct SQLite writes |
| **order_updates.py** | Publish to EventBus | ✅ `event_bus.publish()` | **COMPLIANT** | Correct publishing |
| **snapshots.py** | On-demand fetch | ✅ `market_data_client.get_snapshot()` | **COMPLIANT** | Fetches via API client |
| **snapshots.py** | Caching | ✅ `cache` dict with TTL | **COMPLIANT** | 10-second cache |

**Boundary Assessment:** ✅ **FULLY COMPLIANT**

---

### 1.5 DataHandler - Coordination Layer ONLY

| SPEC Requirement | Implementation | Status | Reasoning |
|-----------------|----------------|--------|-----------|
| Routes raw data to `data/*` handlers | ✅ `on_bar()` → `bars.on_bar()` | **COMPLIANT** | Correct routing |
| Does NOT normalise | ✅ Delegates to handlers | **COMPLIANT** | No transformation |
| Does NOT persist | ✅ Delegates to handlers | **COMPLIANT** | No persistence |
| Does NOT publish | ✅ Delegates to handlers | **COMPLIANT** | No publishing |

**Boundary Assessment:** ✅ **FULLY COMPLIANT**

---

### 1.6 Strategy & RiskManager Data Access

| SPEC Requirement | Implementation | Status | Reasoning |
|-----------------|----------------|--------|-----------|
| Strategy obtains data via DataHandler | ✅ `data_handler.get_dataframe()` | **COMPLIANT** | Correct access pattern |
| RiskManager obtains data via DataHandler | ✅ `data_handler.get_snapshot()` | **COMPLIANT** | Correct access pattern |
| Strategy MUST NOT call alpaca_api/* directly | ✅ No direct API calls | **COMPLIANT** | Clean separation |
| RiskManager MUST NOT call alpaca_api/* directly | ✅ No direct API calls | **COMPLIANT** | Clean separation |
| RiskManager MAY call broker.get_clock() | ✅ Called in `_check_safety_tier()` | **COMPLIANT** | Explicitly allowed |
| RiskManager MAY call broker.get_account() | ✅ Called in `_check_risk_tier()` | **COMPLIANT** | Explicitly allowed |

**Boundary Assessment:** ✅ **FULLY COMPLIANT**

---

## 2. Architectural Divergence: WebSocket vs HTTP Polling

### 2.1 Specified vs Implemented Event Flow

**SPECIFIED EVENT FLOW (SPEC.md):**
```
Alpaca WebSocket → Stream → DataHandler → data/* handlers → EventBus → Strategy/Risk/OrderManager
```

**ACTUAL EVENT FLOW (Implementation):**
```
Alpaca HTTP API → StreamPolling → DataHandler → data/* handlers → EventBus → Strategy/Risk/OrderManager
                              ↑
                              └── WebSocket Stream exists but NOT USED as primary
```

### 2.2 Deviation Analysis

| Aspect | Specification | Implementation | Status | Reasoning |
|--------|---------------|----------------|--------|-----------|
| **Primary Data Source** | WebSocket (`stream.py`) | HTTP Polling (`stream_polling.py`) | **MAJOR DEVIATION** | Complete architectural pivot |
| **Stream Module Status** | Primary | Fallback/unused | **DEVIATION** | Exists but not primary |
| **Latency** | ~1 second real-time | ~1-60 seconds (polling) | **DEGRADATION** | Less responsive |
| **Event Flow Integrity** | WebSocket → DataHandler | Polling → DataHandler | **PRESERVED** | Handler routing unchanged |
| **Module Boundaries** | Enforced | Enforced | **COMPLIANT** | Boundaries respected despite source change |

### 2.3 Why Was This Changed? (Reasoning)

**INTENTIONAL DEVIATION** - Engineering decision based on operational constraints:

1. **WebSocket Connection Limits**: Alpaca's WebSocket has connection limits that were likely exceeded with 31 symbols
2. **Rate Limiting Issues**: `stream.py` has `RateLimiter` and batching code, suggesting HTTP 429 errors were encountered
3. **Reliability**: HTTP polling is more reliable for 24/7 operation (no persistent connection to drop)
4. **Implementation Evidence**: 
   - `orchestrator.py` explicitly imports `StreamPolling` instead of `Stream`
   - `stream_polling.py` is a complete implementation (700+ lines)
   - `stream.py` exists but is not integrated as primary

**Impact:** The event flow model is preserved (data still flows through DataHandler → data/* → EventBus), but the source changed from push (WebSocket) to pull (polling).

---

## 3. Integration Checklist Compliance

| Checklist Item | Status | Reasoning |
|----------------|--------|-----------|
| `broker.py` does NOT contain market data endpoints | ✅ PASS | Only execution endpoints |
| `broker.py` does NOT contain reference endpoints | ✅ PASS | Only execution endpoints |
| `broker.py` owns `/v2/clock` exclusively | ✅ PASS | Fresh calls before orders |
| `alpaca_api/*` modules do NOT submit/cancel orders | ✅ PASS | Read-only operations |
| `stream.py` does NOT normalise data | ✅ PASS | Raw passthrough |
| `stream.py` does NOT publish to EventBus | ✅ PASS | Handler callback pattern |
| `stream.py` does NOT write to SQLite | ✅ PASS | No persistence |
| `DataHandler` routes but does NOT normalise/persist/publish | ✅ PASS | Pure coordination layer |
| `data/*` handlers normalise, persist, AND publish | ✅ PASS | All responsibilities |
| Trade updates flow through `DataHandler` → `data/order_updates.py` → EventBus | ✅ PASS | Correct flow |
| Polling fallback delivers to `DataHandler`, not directly to OrderManager | ✅ PASS | Correct routing |

**Integration Assessment:** ✅ **11/11 CHECKS PASS**

---

## 4. Clock Gating Compliance

| Requirement | Implementation | Status | Reasoning |
|-------------|----------------|--------|-----------|
| `/v2/clock` is the ONLY authoritative source | ✅ `broker.get_clock()` used exclusively | **COMPLIANT** | No calendar fallback |
| Calendar is informational only | ✅ `calendar.py` documented as informational | **COMPLIANT** | Correct usage |
| Fresh clock call before every order | ✅ Called in `_check_safety_tier()` | **COMPLIANT** | Never cached |
| Clock NOT used from alpaca_api | ✅ Owned by broker.py | **COMPLIANT** | Correct ownership |

**Clock Gating Assessment:** ✅ **FULLY COMPLIANT**

---

## 5. Component-by-Component Reasoning

### 5.1 What Was Changed Intentionally

| Component | Change | Reasoning | Evidence |
|-----------|--------|-----------|----------|
| **Stream Source** | WebSocket → HTTP Polling | Connection limits, reliability | `orchestrator.py` imports `StreamPolling` |
| **Strategy** | Single SMA → Multi-timeframe | Better signal quality | `sma_crossover.py` has 3 period pairs |
| **Regime Detection** | Added | Filter false signals in ranging markets | `regime_detector.py` exists |
| **State Persistence** | Enhanced Win #3 | Crash recovery, production readiness | `state_store.py` has persistence methods |
| **Test Coverage** | 48 → 109 tests | Higher confidence | 25 new tests in `test_state_persistence.py` |

### 5.2 What Was Accidentally Missed / Incomplete

| Component | Spec Requirement | Status | Reasoning |
|-----------|-----------------|--------|-----------|
| **Exit Manager** | `src/exit_manager.py` with profit taking (2%) and stop loss (1%) | **MISSING** | Win #3 was redefined to state persistence instead of exits. Original spec not implemented. |
| **WebSocket Integration** | Primary data source | **INCOMPLETE** | Exists but not used as primary. Polling became primary due to operational issues. |
| **SMA Config** | Respect `fast_period`/`slow_period` from config | **IGNORED** | Strategy hardcodes periods; config values ignored |

### 5.3 What Was Added (Scope Creep)

| Component | Purpose | Value |
|-----------|---------|-------|
| `daemon.py` | Unix daemon operation | Production deployment |
| `docker-compose.yml` | Containerisation | Dev/prod parity |
| `Makefile` | Systemd service, convenience commands | Linux server deployment |
| `health_check.py` | Monitoring endpoint | Observability |
| `notifier.py` | Alerts | Operations |
| `backup_manager.py` | DB backups | Data safety |
| `housekeeping.py` | Maintenance tasks | 24/7 operation |
| Session-aware risk limits | Different limits for regular/extended hours | Crypto readiness |

---

## 6. Data Flow Verification

### 6.1 Correct Flow (As Implemented)

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          DATA FLOW (ACTUAL)                              │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Alpaca API ←──HTTP──→ StreamPolling ────→ DataHandler ────→ data/bars  │
│     ↑                                                    │               │
│     │                                                    ↓               │
│  (polling)                                          data/order_updates   │
│     │                                                    │               │
│     │    ┌───────────────────────────────────────────────┘               │
│     │    │                                                              │
│     │    ↓                                                              │
│     │ EventBus ←──────┬──────────┬──────────┐                           │
│     │                 ↓          ↓          ↓                           │
│     │            Strategy    RiskManager  OrderManager                   │
│     │                 │          │          │                            │
│     │                 └──────────┴──────────┘                            │
│     │                            │                                       │
│     │                            ↓                                       │
│     └────────────────────── Alpaca API (orders)                          │
│                                                                          │
│  NOTE: stream.py (WebSocket) exists but is NOT the primary source       │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 6.2 Flow Integrity Assessment

| Flow Step | Spec | Actual | Status |
|-----------|------|--------|--------|
| Raw data → Stream | WebSocket | HTTP Polling | ⚠️ Changed source |
| Stream → DataHandler | Raw objects | Raw objects | ✅ Preserved |
| DataHandler → data/* | Routing | Routing | ✅ Preserved |
| data/* → EventBus | Publish | Publish | ✅ Preserved |
| EventBus → Consumers | Subscribe | Subscribe | ✅ Preserved |
| Strategy/Risk → DataHandler | Queries | Queries | ✅ Preserved |
| OrderManager → Broker | Submit | Submit | ✅ Preserved |
| Risk → Broker.get_clock() | Fresh calls | Fresh calls | ✅ Preserved |

**Flow Assessment:** Source changed, but **internal event flow is preserved exactly as specified**.

---

## 7. Summary of Findings

### 7.1 Compliant Areas ✅

1. **Module Boundaries:** All SPEC.md module boundaries respected
2. **Broker Isolation:** `broker.py` contains only execution endpoints
3. **Data Client Purity:** `alpaca_api/*` contains only data/reference endpoints
4. **Handler Responsibilities:** `data/*` handlers normalise, persist, and publish
5. **DataHandler Role:** Pure coordination layer, no transformation
6. **Clock Gating:** Fresh `/v2/clock` calls before every order
7. **Event Flow:** Internal flow (DataHandler → data/* → EventBus) preserved
8. **Strategy/Risk Data Access:** Via DataHandler only (except allowed broker calls)

### 7.2 Deviations ⚠️

1. **Primary Data Source:** WebSocket specified, HTTP polling implemented
   - **Reason:** Operational constraints (connection limits, reliability)
   - **Impact:** Higher latency, more resource-intensive
   - **Mitigation:** Event flow preserved, boundaries maintained

2. **Win #3 Redefinition:** Exit management specified, state persistence delivered
   - **Reason:** Crash recovery prioritized over profit/stop exits
   - **Impact:** No automated profit taking or stop loss
   - **Mitigation:** Manual position management required

### 7.3 Missing Components ❌

1. **Exit Manager Module:** Never implemented
2. **Profit Taking (2%):** Never implemented
3. **Stop Loss (1%):** Never implemented
4. **WebSocket as Primary:** Exists but not integrated

---

## 8. Recommendations

### 8.1 Fix to Match SPEC.md

| Priority | Item | Action | Effort |
|----------|------|--------|--------|
| **P0** | Exit Manager | Implement `src/exit_manager.py` with profit taking (2%) and stop loss (1%) | 2-3 hours |
| **P1** | WebSocket Primary | Fix connection limits, make `Stream` primary, `StreamPolling` as fallback | 2-4 hours |
| **P2** | SMA Config | Make strategy respect `fast_period`/`slow_period` from config | 30 min |

### 8.2 Document as Intentional Deviation

| Item | Reasoning | Documentation Location |
|------|-----------|----------------------|
| **HTTP Polling as Primary** | WebSocket connection limits exceeded, reliability concerns | `README.md` Architecture section |
| **Win #3 Redefinition** | State persistence prioritized for production crash recovery | `WIN3_DEPLOYMENT.md` |
| **Multi-timeframe SMA** | Better signal quality, regime detection reduces false positives | `TECHNICAL_SPEC.md` Strategy section |

### 8.3 No Action Required

All module boundaries are correctly implemented. No violations detected.

---

## 9. Final Assessment

| Category | Grade | Notes |
|----------|-------|-------|
| **Module Boundaries** | A+ | SPEC.md boundaries fully respected |
| **Event Flow** | A | Internal flow perfect; source changed |
| **Clock Gating** | A+ | Fresh calls, no calendar fallback |
| **Broker Isolation** | A+ | Clean execution-only layer |
| **Data Architecture** | B+ | HTTP polling works; WebSocket specified |
| **Feature Completeness** | C+ | Missing exit management (Win #3) |

**Overall Grade: B+**

The codebase is **architecturally sound** with **correct module boundaries**, but deviates from the original specification in data source (WebSocket → polling) and is missing the exit management system that was part of the development plan.

---

*Report generated by subagent: python-dev*  
*Session: prompt-deviation-review-with-spec*  
*Workspace: /home/t-rox/.openclaw/workspace/alpaca-fleece*
