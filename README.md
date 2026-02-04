# Alpaca Trading Bot

Event-driven algorithmic trading bot for Alpaca Markets using WebSocket streaming and Python asyncio.

## Features

- **Event-Driven Architecture**: WebSocket streaming with asyncio for real-time market data processing
- **Safety-First Design**:
  - Paper trading by default
  - Kill-switch mechanism (environment variable or file)
  - Circuit breaker with persistence across restarts
  - Deterministic order IDs prevent duplicate trades
  - Market hours enforcement
  - Position sizing and daily loss limits
- **Robust Operation**:
  - Automatic reconnection with exponential backoff
  - Backfill missed bars after reconnection
  - State persistence to SQLite
  - Comprehensive logging (JSON + human-readable)
- **SMA Crossover Strategy**: Simple Moving Average crossover with configurable periods
- **Comprehensive Testing**: pytest suite with >90% coverage

## Project Structure

```
alpaca-bot/
├── src/
│   ├── config.py              # Configuration management
│   ├── broker.py              # Alpaca REST API wrapper
│   ├── stream.py              # WebSocket streaming
│   ├── event_bus.py           # Event system
│   ├── data_handler.py        # Rolling data windows
│   ├── strategy/
│   │   ├── base.py            # Strategy interface
│   │   └── sma_crossover.py  # SMA crossover implementation
│   ├── risk_manager.py        # Risk management
│   ├── order_manager.py       # Order execution
│   ├── state_store.py         # SQLite persistence
│   └── logger.py              # Structured logging
├── tests/                     # Test suite
├── main.py                    # Entry point
├── pyproject.toml            # Project configuration (uv, pytest, ruff, pyright)
├── .pre-commit-config.yaml   # Pre-commit hooks configuration
├── .env.example              # Configuration template
└── README.md                 # This file
```

## Setup

### 1. Install uv

```bash
# macOS/Linux
curl -LsSf https://astral.sh/uv/install.sh | sh

# Windows
powershell -ExecutionPolicy BypassUser -c "irm https://astral.sh/uv/install.ps1 | iex"

# Or using pip
pip install uv
```

### 2. Install Dependencies

```bash
uv sync
```

This creates a virtual environment and installs all dependencies from `pyproject.toml`.

### 3. Configure Environment

```bash
cp .env.example .env
```

Edit `.env` and add your Alpaca API credentials:

```bash
ALPACA_API_KEY=your_paper_api_key_here
ALPACA_SECRET_KEY=your_paper_secret_key_here
```

Get paper trading API keys from: https://app.alpaca.markets/paper/dashboard/overview

### 4. Configure Trading Parameters

Edit `.env` to customise:

- **Symbols**: `SYMBOLS=AAPL,MSFT,GOOGL`
- **Strategy**: `SMA_FAST=10`, `SMA_SLOW=30`
- **Risk Limits**: `MAX_POSITION_PCT=0.10`, `MAX_DAILY_LOSS_PCT=0.05`

## Running the Bot

### Paper Trading (Default)

```bash
python main.py
```

The bot will:
1. Load configuration and validate settings
2. Reconcile account state with Alpaca
3. Check circuit breaker status
4. Start WebSocket stream for market data
5. Process bars through strategy → risk → order pipeline
6. Log activity to console and `logs/alpaca-bot.log`

### Dry Run Mode

Test without submitting orders:

```bash
# Add to .env
DRY_RUN=true

python main.py
```

### Live Trading (USE WITH CAUTION)

**WARNING: Live trading uses real money. Test thoroughly with paper trading first.**

Requirements:
1. Set `ALPACA_PAPER=false` in `.env`
2. Set `ALLOW_LIVE_TRADING=true` in `.env`
3. Add live API credentials

```bash
# .env
ALPACA_API_KEY=your_live_api_key
ALPACA_SECRET_KEY=your_live_secret_key
ALPACA_PAPER=false
ALLOW_LIVE_TRADING=true

python main.py
```

Both flags are required to prevent accidental live trading.

## Safety Features

### Kill Switch

Immediately stop all trading:

**Option 1: Environment Variable**
```bash
KILL_SWITCH=true
```

**Option 2: Create File**
```bash
touch .kill_switch
```

### Circuit Breaker

Automatically trips after 5 consecutive failures (order rejections, API errors, etc.).

**Check Status**: View `data/bot_state.db` or check logs for "CIRCUIT BREAKER TRIPPED"

**Reset**:
1. Fix underlying issues
2. Set `CIRCUIT_BREAKER_RESET=true` in `.env`
3. Restart bot

**Manual Reset via Database**:
```bash
sqlite3 data/bot_state.db
DELETE FROM bot_state WHERE key = 'circuit_breaker_state';
```

### Market Hours

By default, only trades 9:30 AM - 4:00 PM ET on weekdays.

Enable extended hours:
```bash
ALLOW_EXTENDED_HOURS=true
```

## Strategy Development

### Creating a New Strategy

1. Create file in `src/strategy/your_strategy.py`
2. Inherit from `BaseStrategy`
3. Implement required methods:

```python
from src.strategy.base import BaseStrategy
from src.event_bus import SignalEvent

class YourStrategy(BaseStrategy):
    def __init__(self, state_store, **params):
        super().__init__(name="YourStrategy")
        self.state_store = state_store
        # Set up parameters

    def get_required_history(self) -> int:
        return 50  # Minimum bars needed

    def on_bar(self, symbol: str, df: pd.DataFrame) -> Optional[SignalEvent]:
        # Process data, return SignalEvent or None
        if len(df) < self.get_required_history():
            return None

        # Your logic here
        if buy_condition:
            return SignalEvent(
                symbol=symbol,
                side="buy",
                strategy_name=self.name,
                signal_timestamp=df.index[-1],
            )

        return None
```

4. Update `main.py` to use your strategy
5. Add tests in `tests/test_your_strategy.py`

**Important**: Strategies must not emit duplicate consecutive signals. Track last signal per symbol via `state_store`.

## Testing

### Run All Tests

```bash
pytest tests/ -v
```

### Run Specific Test File

```bash
pytest tests/test_strategy.py -v
```

### Run with Coverage

```bash
pytest tests/ --cov=src --cov-report=html
```

## Database Schema

SQLite database at `data/bot_state.db`:

### Tables

- **order_intents**: All order attempts (pending, filled, rejected, etc.)
- **trades**: Executed trades with prices
- **equity_curve**: Equity snapshots every 60s
- **bot_state**: Key-value store for circuit breaker, trade counts, last signals

### Useful Queries

```sql
-- View recent trades
SELECT * FROM trades ORDER BY timestamp DESC LIMIT 10;

-- Check circuit breaker status
SELECT * FROM bot_state WHERE key = 'circuit_breaker_state';

-- View equity curve
SELECT timestamp, equity, daily_pnl FROM equity_curve ORDER BY timestamp DESC LIMIT 20;

-- Daily trade count
SELECT value FROM bot_state WHERE key = 'daily_trade_count';
```

## Logs

Logs are written to:
- **Console**: Human-readable format with colours
- **File**: `logs/alpaca-bot.log` (JSON format, daily rotation, 30 days retention)

Each log entry includes:
- `timestamp`: UTC timestamp
- `run_id`: Unique ID for this execution
- `level`: Log level (DEBUG, INFO, WARNING, ERROR, CRITICAL)
- `message`: Log message
- `symbol`, `side`, `qty`, etc.: Contextual fields

## Configuration Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `ALPACA_API_KEY` | - | Alpaca API key (required) |
| `ALPACA_SECRET_KEY` | - | Alpaca secret key (required) |
| `ALPACA_PAPER` | `true` | Use paper trading |
| `ALLOW_LIVE_TRADING` | `false` | Enable live trading (requires ALPACA_PAPER=false) |
| `SYMBOLS` | `AAPL,MSFT` | Comma-separated symbols |
| `BAR_TIMEFRAME` | `1Min` | Bar timeframe (1Min, 5Min, 15Min, 1Hour, 1Day) |
| `STREAM_FEED` | `iex` | Stream feed (iex or sip) |
| `MAX_POSITION_PCT` | `0.10` | Max position size as % of equity |
| `MAX_DAILY_LOSS_PCT` | `0.05` | Max daily loss as % of equity |
| `MAX_TRADES_PER_DAY` | `20` | Max trades per day |
| `SMA_FAST` | `10` | Fast SMA period |
| `SMA_SLOW` | `30` | Slow SMA period |
| `DRY_RUN` | `false` | Log orders without submitting |
| `ALLOW_EXTENDED_HOURS` | `false` | Trade outside 9:30-16:00 ET |
| `LOG_LEVEL` | `INFO` | Log level (DEBUG, INFO, WARNING, ERROR) |
| `KILL_SWITCH` | - | Set to `true` to disable trading |
| `CIRCUIT_BREAKER_RESET` | `false` | Reset circuit breaker on startup |

## Troubleshooting

### Bot won't start

1. Check `.env` has valid API keys
2. Check circuit breaker: `sqlite3 data/bot_state.db "SELECT * FROM bot_state WHERE key = 'circuit_breaker_state';"`
3. Check logs in `logs/alpaca-bot.log`

### No signals generated

1. Check if strategy has sufficient data: requires `SMA_SLOW + 2` bars minimum
2. Verify symbols have active trading (check Alpaca dashboard)
3. Check market hours (9:30-16:00 ET) unless `ALLOW_EXTENDED_HOURS=true`
4. Review strategy logic with `LOG_LEVEL=DEBUG`

### Orders not filled

1. Check dry run mode: `DRY_RUN=false`
2. Verify not paper trading when expecting live: `ALPACA_PAPER=false`
3. Check account status: trading_blocked, account_blocked
4. Review logs for rejection reasons

### WebSocket disconnects

- Normal behavior, bot automatically reconnects with exponential backoff
- After 10 consecutive failures, circuit breaker trips
- Check network connectivity and Alpaca status

## Architecture

```
WebSocket Stream → Event Bus → Data Handler → Strategy
                                                  ↓
                                            Signal Event
                                                  ↓
                                           Risk Manager
                                                  ↓
                                          Order Manager
                                                  ↓
                                        SQLite State Store
```

### Event Flow

1. **WebSocket** receives bar → publishes `MarketBarEvent`
2. **Data Handler** maintains rolling window per symbol
3. **Strategy** analyses bars → emits `SignalEvent` on crossover
4. **Risk Manager** validates signal (kill-switch, circuit breaker, limits)
5. **Order Manager** generates deterministic `client_order_id`, checks for duplicates
6. **State Store** persists order before submission
7. **Broker** submits order to Alpaca (if not dry run)

### State Persistence

All critical state persists to SQLite:
- Order intents (prevents duplicates after restart)
- Circuit breaker state
- Daily trade count
- Last signal per symbol (prevents duplicate signals)
- Equity snapshots
- Trade history

## License

MIT

## Disclaimer

**This software is for educational purposes only. USE AT YOUR OWN RISK.**

- Past performance does not guarantee future results
- Trading involves substantial risk of loss
- Test thoroughly with paper trading before using real money
- The authors assume no liability for financial losses
- You are responsible for compliance with applicable laws and regulations

## Support

For issues or questions:
- Review logs in `logs/alpaca-bot.log`
- Check Alpaca API status: https://status.alpaca.markets/
- Consult Alpaca documentation: https://docs.alpaca.markets/

## Credits

Built with:
- [alpaca-py](https://github.com/alpacahq/alpaca-py) - Alpaca Markets Python SDK
- [pandas](https://pandas.pydata.org/) - Data analysis
- [pandas-ta](https://github.com/twopirllc/pandas-ta) - Technical analysis
