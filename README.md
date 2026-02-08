# Alpaca Fleece Trading Bot

A production-ready, event-driven trading bot for Alpaca Markets. Implements SMA crossover strategy with multi-timeframe analysis, comprehensive risk management, and 24/7 operation capability.

## Features

- **Multi-Timeframe SMA Strategy**: Fast (10-min), medium (30-min), and slow (90-min) SMA crossovers
- **Risk Management**: Kill switch, circuit breaker, daily loss limits, position sizing
- **State Persistence**: SQLite database survives restarts
- **HTTP Polling**: Reliable market data via Alpaca API (avoids WebSocket limits)
- **24/7 Operation**: Daemon mode with graceful shutdown handling
- **Paper Trading**: Locked to paper mode for safe testing

## Quick Start

### Prerequisites

- Python 3.12+
- Alpaca API credentials (paper trading)
- uv (for dependency management)

### Dependency pinning & regeneration (pip-tools)

This project uses `pip-tools` to generate pinned, hashed `requirements.txt` artifacts used by CI for reproducible installs.

- Recommended (example) tools to match CI: `pip==23.2.1`, `pip-tools==7.5.2`.
- To regenerate the pinned/hashes files locally (developer machine):

```bash
# from project root, with a clean virtualenv activated
pip install "pip==23.2.1" "pip-tools==7.5.2"
pip-compile --output-file=requirements.txt requirements.in --generate-hashes
pip-compile --output-file=requirements-dev.txt requirements-dev.in --generate-hashes
```

- Note: CI installs production `requirements.txt` with `--require-hashes`. Installing `requirements-dev.txt` with `--require-hashes` may fail because some build-time transitive packages (e.g., `setuptools`) are not hashed; CI installs dev deps without `--require-hashes` to avoid that failure.


### Installation

```bash
# Clone and enter directory
cd alpaca-fleece

# Install dependencies
uv sync --frozen

# Configure environment
cp .env.example .env
# Edit .env with your Alpaca API credentials
```

## Running Options

### Option 1: Direct Python (Development)

Best for development and debugging:

```bash
source .venv/bin/activate
python orchestrator.py
```

The bot runs in the foreground. Press Ctrl+C for graceful shutdown.

### Option 2: Shell Script (Recommended for Production)

Uses `setsid` to create a new session, ensuring the bot survives terminal disconnects:

```bash
# Start the bot
./bot.sh start

# Check status
./bot.sh status

# View logs
tail -f logs/orchestrator.out

# Stop gracefully
./bot.sh stop

# Restart
./bot.sh restart
```

**Why this works:** `setsid` creates a new session and process group, so the bot is no longer associated with the terminal's session.

### Option 3: Docker (Containerised Deployment)

Best for isolated environments and easy deployment:

```bash
# Build the Docker image
make build

# Run the container
make run

# Check container status
make status

# View logs
make logs

# Stop the container
make stop

# Restart
make restart
```

**Docker Features:**
- Uses `python:3.12-slim` base image
- Includes `uv` for fast dependency resolution
- Mounts `data/` and `logs/` as volumes for persistence
- Runs as non-root user for security

**Manual Docker Commands:**

```bash
# Build
docker-compose build

# Run detached
docker-compose up -d

# View logs
docker-compose logs -f

# Stop
docker-compose down

# Shell access
docker-compose exec alpaca-bot bash
```

### Option 4: Python Daemon (Alternative)

Uses double-fork technique for proper Unix daemonisation:

```bash
# Start daemon
python daemon.py start

# Check status
python daemon.py status

# Stop
python daemon.py stop
```

**Features:**
- PID file management
- Signal handling (SIGTERM for graceful shutdown)
- Proper file descriptor handling
- Background operation

### Option 5: Systemd Service (Linux Servers)

For production Linux servers with automatic restart:

```bash
# Install service (one-time)
make systemd-install

# Start
make systemd-start

# Enable auto-start on boot
make systemd-enable

# Check status
make systemd-status

# View logs
journalctl -u alpaca-bot -f
```

**Benefits:**
- Automatic restart on crashes
- Structured logging via journald
- Dependency management (waits for network)
- Clean integration with Linux systems

### Systemd: BOT_ROOT and Environment overrides

The included unit file supports an override `EnvironmentFile` and a `BOT_ROOT` environment variable so administrators can choose the runtime install location without editing the unit file. Example override file (create `/etc/default/alpaca-bot` as root):

```bash
# /etc/default/alpaca-bot
# Absolute path to the bot installation directory (contains .venv, orchestrator.py, data/, logs/)
BOT_ROOT=/path/to/alpaca-fleece
```

After creating or updating the override file, reload and restart the systemd unit:

```bash
sudo systemctl daemon-reload
sudo systemctl restart alpaca-bot
sudo systemctl status alpaca-bot
```

Notes:
- The unit uses `${BOT_ROOT}` for `ExecStart`, `PATH`, `ReadWritePaths`, and log file paths. Ensure the user running the service has access to `${BOT_ROOT}/data` and `${BOT_ROOT}/logs`.
- Logs are appended to `${BOT_ROOT}/logs/orchestrator.out` (or visible via `journalctl -u alpaca-bot`).


## Configuration

### Environment Variables (`.env`)

```bash
# Alpaca API credentials (required)
ALPACA_API_KEY=your_api_key_here
ALPACA_SECRET_KEY=your_secret_key_here

# Trading mode (LOCKED to paper)
ALPACA_PAPER=true
ALLOW_LIVE_TRADING=false

# Safety gates
KILL_SWITCH=false
CIRCUIT_BREAKER_RESET=false
DRY_RUN=false

# Logging
LOG_LEVEL=INFO

# Paths (relative to bot directory)
DATABASE_PATH=data/trades.db
CONFIG_PATH=config/trading.yaml
```

### Trading Configuration (`config/trading.yaml`)

```yaml
# Symbols to trade
symbols:
  mode: explicit
  list: [AAPL, MSFT, GOOGL, NVDA, QQQ, SPY, TSLA, AMD, AMZN, META, NFLX, UBER, COIN, MSTR, ARKK, IWM, EEM, GLD, TLT, USO, RTX, LMT, NOC, BA, GD, GOLD, SLV, PAAS, HL, SCCO, FCX]

# Trading hours
session_policy: regular_only  # 9:30-16:00 ET only

# Risk limits
risk:
  max_position_pct: 0.10      # 10% max per position
  max_daily_loss_pct: 0.05    # 5% daily loss limit
  max_trades_per_day: 20
  max_concurrent_positions: 10

# Strategy parameters
strategy:
  name: sma_crossover
  # Uses 10-min, 30-min, and 90-min SMAs (multi-timeframe)
```

## Monitoring

### Check Bot Status

```bash
./bot.sh status
```

### View Logs

```bash
# Real-time logs
tail -f logs/orchestrator.out

# Last 100 lines
tail -100 logs/orchestrator.out

# Search for errors
grep ERROR logs/orchestrator.out
```

### Database Queries

```bash
# Check trades
sqlite3 data/trades.db "SELECT * FROM trades ORDER BY timestamp_utc DESC LIMIT 10;"

# Check equity curve
sqlite3 data/trades.db "SELECT * FROM equity_curve ORDER BY timestamp_utc DESC LIMIT 10;"

# Check open orders
sqlite3 data/trades.db "SELECT * FROM order_intents WHERE status IN ('new', 'submitted', 'accepted');"
```

### Process Monitoring

```bash
# Check if running
ps aux | grep orchestrator

# Check with PID file
cat data/alpaca_bot.pid

# Check resource usage
top -p $(cat data/alpaca_bot.pid)
```

## Testing

Run the test suite:

```bash
# All tests
.venv/bin/python -m pytest tests/ -v

# Quick check
.venv/bin/python -m pytest tests/ -q

# Specific test
.venv/bin/python -m pytest tests/test_strategy.py -v

# With coverage
.venv/bin/python -m pytest tests/ --cov=src
```

**Current Status:** 109 tests passing

## Architecture

The bot uses a phase-based architecture matching agent contracts:

1. **Phase 1: Infrastructure** — Load config, connect broker, initialise state store, run reconciliation
2. **Phase 2: Data Layer** — Start event bus, initialise streaming (HTTP polling), set up data handlers
3. **Phase 3: Trading Logic** — Load strategy, initialise risk manager, order manager, housekeeping
4. **Phase 4: Runtime** — Start event processing loop, monitor tasks, handle graceful shutdown

## Troubleshooting

### Bot Won't Start

```bash
# Check logs for errors
tail -50 logs/orchestrator.out

# Verify environment
cat .env | grep ALPACA_API_KEY

# Check for existing process
pgrep -f orchestrator

# Clean stale PID file
rm data/alpaca_bot.pid
```

### Bot Stops Unexpectedly

- Check disk space: `df -h`
- Check memory: `free -h`
- Look for errors in logs: `grep ERROR logs/orchestrator.out`
- Verify Alpaca API connectivity

### Database Issues

```bash
# Reset database (WARNING: loses all data)
rm data/trades.db
# Bot will recreate on next start
```

### Permission Errors

```bash
# Fix data directory permissions
chmod 755 data/
chmod 644 data/*.db 2>/dev/null || true
```

## Development

### Project Structure

```
alpaca-fleece/
├── src/                    # Source code
│   ├── alpaca_api/        # Alpaca API clients
│   ├── data/              # Data normalisation
│   ├── strategy/          # Trading strategies
│   ├── broker.py          # Order execution
│   ├── config.py          # Configuration loading
│   ├── event_bus.py       # Async event system
│   ├── housekeeping.py    # Maintenance tasks
│   ├── order_manager.py   # Order lifecycle
│   ├── reconciliation.py  # Account sync
│   ├── risk_manager.py    # Risk enforcement
│   ├── state_store.py     # SQLite persistence
│   └── stream_polling.py  # HTTP market data
├── tests/                 # Test suite (109 tests)
├── config/                # Trading configuration
├── data/                  # SQLite database (gitignored)
├── logs/                  # Log files (gitignored)
├── agents/                # Architecture docs (gitignored)
├── bot.sh                 # Shell script runner
├── daemon.py              # Python daemon wrapper
├── orchestrator.py        # Main entry point
├── Dockerfile             # Docker image
├── docker-compose.yml     # Docker orchestration
└── Makefile               # Convenience commands
```

### Making Changes

1. Edit source code in `src/`
2. Run tests: `.venv/bin/python -m pytest tests/`
3. Test manually: `python orchestrator.py`
4. Use `bot.sh` for production deployment

## Safety Features

- **Paper Trading Only**: Locked to Alpaca paper trading (cannot enable live)
- **Kill Switch**: Set `KILL_SWITCH=true` in `.env` to halt all trading
- **Circuit Breaker**: Halts after 5 consecutive order failures
- **Daily Limits**: Enforces max daily loss and trade count
- **Reconciliation**: Validates state against Alpaca on startup
- **Graceful Shutdown**: Closes positions and cancels orders on SIGTERM
