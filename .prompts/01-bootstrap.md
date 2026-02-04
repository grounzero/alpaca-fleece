You are Claude Code acting as a senior production engineer.

Build a production-ready, event-driven Alpaca trading bot in Python using WebSocket streaming.


### HARD CONSTRAINTS (NON-NEGOTIABLE)


SAFETY
- Paper trading ONLY unless BOTH: ALPACA_PAPER=false AND ALLOW_LIVE_TRADING=true
- Kill-switch: KILL_SWITCH=true OR .kill_switch file exists → refuse to trade
- Circuit breaker: 5 consecutive failures → halt, require manual reset
  - State must persist to SQLite and survive restarts
  - Reset requires manual intervention (delete state row or set CIRCUIT_BREAKER_RESET=true)
- Deterministic client_order_id prevents duplicate orders (hash of: strategy, symbol, timeframe, signal_ts, side)
- Persist state to SQLite; restarts must not cause duplicate trades
- All market logic uses America/New_York timezone
- No trading outside 09:30-16:00 ET unless ALLOW_EXTENDED_HOURS=true
- Never log API keys, secrets, or full environment variables
- On startup, bot MUST reconcile Alpaca account, positions, and open orders before processing any new market data

TECH STACK (DO NOT CHANGE)
- Python 3.11+, alpaca-py, pandas, pandas-ta, SQLite, python-dotenv, pytest
- requirements.txt (not Poetry)
- asyncio for concurrency


### ARCHITECTURE OVERVIEW

Alpaca WebSocket → MarketStream → EventBus (asyncio.Queue)
↓
DataHandler (rolling window)
↓
Strategy (signal generation)
↓
RiskManager (validation + sizing)
↓
OrderManager (execution + idempotency)
↓
SQLite (state persistence)

The bot is a single async process with three concurrent tasks:
1. Stream consumer (WebSocket → EventBus)
2. Event processor (EventBus → Strategy → Risk → Orders)
3. Housekeeping (equity snapshots every 60s, heartbeat logging)

After any WebSocket reconnect, the bot must backfill missed bars via REST before resuming normal signal generation.


### PROJECT STRUCTURE


alpaca-bot/
├── src/
│   ├── config.py           # Load .env, validate, expose frozen config
│   ├── broker.py           # Alpaca REST wrapper with retry logic
│   ├── stream.py           # WebSocket connection + reconnect logic
│   ├── event_bus.py        # Async queue + event dataclasses
│   ├── data_handler.py     # Rolling window per symbol → DataFrame
│   ├── strategy/
│   │   ├── base.py         # Abstract interface
│   │   └── sma_crossover.py
│   ├── risk_manager.py     # Limits, kill-switch, circuit breaker, position sizing
│   ├── order_manager.py    # Idempotent execution, lifecycle tracking
│   ├── state_store.py      # SQLite persistence
│   └── logger.py           # JSON structured logs, daily rotation
├── tests/
├── main.py                 # Async orchestration
├── .env.example
├── requirements.txt
└── README.md


### CONFIGURATION (.env.example)


# Credentials
ALPACA_API_KEY=
ALPACA_SECRET_KEY=
ALPACA_PAPER=true

# Safety gate (both required for live)
ALLOW_LIVE_TRADING=false

# Trading
SYMBOLS=AAPL,MSFT
BAR_TIMEFRAME=1Min
STREAM_FEED=iex

# Risk limits
MAX_POSITION_PCT=0.10
MAX_DAILY_LOSS_PCT=0.05
MAX_TRADES_PER_DAY=20

# Strategy
SMA_FAST=10
SMA_SLOW=30

# Modes
DRY_RUN=false
ALLOW_EXTENDED_HOURS=false
LOG_LEVEL=INFO


### COMPONENT SPECIFICATIONS


CONFIG (src/config.py)
- Load from .env, validate all required fields, fail fast
- Expose as frozen dataclass
- Method: is_live_trading_enabled() → bool

BROKER (src/broker.py)
- Wrap alpaca-py TradingClient
- Methods: get_account, get_positions, get_open_orders, submit_order, cancel_order, get_bars
- Retry transient errors (3 attempts, exponential backoff)
- Never log credentials

STREAM (src/stream.py)
- Connect to Alpaca StockDataStream, subscribe to bars
- On message: convert to MarketBarEvent, push to EventBus
- Reconnect with exponential backoff + jitter (max 60s delay)
- If no message for 30s, force reconnect
- After 10 consecutive reconnect failures, trip circuit breaker
- After successful reconnect: call broker.get_bars() to backfill missed data

EVENT BUS (src/event_bus.py)
- Define events as dataclasses: MarketBarEvent, SignalEvent, OrderIntentEvent, OrderUpdateEvent
- Single asyncio.Queue with type-based routing
- Methods: publish(event), subscribe(event_type, handler), run(), stop()

DATA HANDLER (src/data_handler.py)
- Maintain rolling deque per symbol (size = slow_sma + 10 buffer)
- On MarketBarEvent: append bar, if enough history → build DataFrame for strategy

STRATEGY (src/strategy/)
- base.py: Abstract class with name, get_required_history(), on_bar(symbol, df) → Optional[SignalEvent]
- sma_crossover.py: Emit BUY/SELL only on crossover events (not every bar where fast > slow)
- Strategies must not emit duplicate consecutive BUY or SELL signals for the same symbol; last action must be tracked via persisted state

RISK MANAGER (src/risk_manager.py)
- Check order: kill-switch, circuit breaker, market hours, daily limits, position size
- Calculate qty: equity * MAX_POSITION_PCT / price, capped by limits
- Track: daily_trade_count, daily_pnl, consecutive_failures
- Methods: validate_signal() → (bool, reason), calculate_qty(), trip_circuit_breaker(), check_circuit_breaker()

ORDER MANAGER (src/order_manager.py)
- Generate deterministic client_order_id (sha256 hash, first 16 chars)
- Before submit: check state_store for existing client_order_id
- If exists: skip (log "duplicate prevented")
- If new: submit via broker, persist immediately
- DRY_RUN mode: log but don't submit
- Track order lifecycle (submitted, filled, cancelled, rejected)
- Partial fills must not trigger duplicate orders; track filled_qty per order

STATE STORE (src/state_store.py)
SQLite tables:
- order_intents (client_order_id PK, symbol, side, qty, status, filled_qty, alpaca_order_id, created_at)
- trades (id, timestamp, symbol, side, qty, price, order_id, client_order_id)
- equity_curve (id, timestamp, equity, daily_pnl)
- bot_state (key PK, value, updated_at)

Keys: last_signal:{symbol}, daily_trade_count, circuit_breaker_state, circuit_breaker_failures

LOGGER (src/logger.py)
- JSON format to file, human-readable to console
- Daily rotation, 30 days retention
- Include run_id in every log entry
- Never log secrets

MAIN (main.py)
Startup:
1. Load config, validate safety gates
2. Init logger, state_store, broker
3. Reconcile: fetch account/positions/orders from Alpaca, log summary, block until complete
4. Check circuit breaker state from SQLite; if tripped, refuse to start
5. Init: event_bus, data_handler, strategy, risk_manager, order_manager, stream
6. Start tasks: stream.run(), event_processor(), housekeeping()
7. On SIGINT/SIGTERM: stop stream, drain queue (5s timeout), persist state, exit


### TESTING REQUIREMENTS


Minimum tests:
- Config validation (valid, missing field, invalid type)
- SMA strategy emits signal only on crossover (not on every bar)
- Strategy does not emit duplicate consecutive signals
- Risk manager blocks on limits (kill-switch, circuit breaker, market hours, daily limits)
- client_order_id is deterministic (same inputs → same hash)
- Duplicate orders prevented via state_store lookup
- Circuit breaker persists across simulated restart

Use pytest + pytest-asyncio. Mock Alpaca API calls.


### README SECTIONS


1. Setup (venv, install, configure .env)
2. Run paper trading: `python main.py`
3. Enable live trading (requires BOTH flags, warning about risk)
4. Safety features (kill-switch, circuit breaker reset procedure)
5. Adding new strategies
6. Running tests


### OUTPUT


Create all files with complete, working code. After generation, print:
SETUP:
python -m venv venv && source venv/bin/activate
pip install -r requirements.txt
cp .env.example .env  # Add Alpaca paper API keys
RUN:     python main.py
TEST:    pytest tests/ -v
LIVE:    Set ALPACA_PAPER=false AND ALLOW_LIVE_TRADING=true (paper test first!)

Choose safest defaults for any ambiguity. Do not ask questions.
