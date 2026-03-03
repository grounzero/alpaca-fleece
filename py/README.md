# Alpaca Fleece (Python)

A production-ready, event-driven trading bot for Alpaca Markets. Implements SMA crossover strategy with multi-timeframe analysis, comprehensive risk management, and 24/7 operation capability.

## Features

- Multi-timeframe SMA strategy (fast, medium, slow)
- Risk management with kill switch and circuit breaker
- SQLite state persistence
- HTTP polling market data
- Daemon mode with graceful shutdown
- Paper trading by default

## Quick Start

### Prerequisites

- Python 3.12+
- Alpaca API credentials (paper trading)
- uv (dependency management)

### Install

```bash
cd py
uv sync --frozen
cp .env.example .env
# Edit .env with your Alpaca API credentials
```

## Running Options

### Option 1: Direct Python (development)

```bash
cd py
source .venv/bin/activate
python orchestrator.py
```

### Option 2: Shell Script (recommended)

```bash
cd py
./bot.sh start
./bot.sh status
./bot.sh stop
```

Logs are written to `logs/orchestrator.out`.

### Option 3: Python Daemon

```bash
cd py
python daemon.py start
python daemon.py status
python daemon.py stop
```

### Option 4: Docker

```bash
docker-compose build
docker-compose up -d
```

## Configuration

### Environment Variables

```bash
ALPACA_API_KEY=your_api_key_here
ALPACA_SECRET_KEY=your_secret_key_here
ALPACA_PAPER=true
ALLOW_LIVE_TRADING=false
KILL_SWITCH=false
CIRCUIT_BREAKER_RESET=false
DRY_RUN=false
LOG_LEVEL=INFO
DATABASE_PATH=data/trades.db
CONFIG_PATH=config/trading.yaml
```

### Trading Configuration

Config lives in `config/trading.yaml` under `py/`.

## Monitoring

```bash
cd py
./bot.sh status

tail -f logs/orchestrator.out
```

Database queries:

```bash
sqlite3 data/trades.db "SELECT * FROM trades ORDER BY timestamp_utc DESC LIMIT 10;"
```

## Testing

```bash
cd py
.venv/bin/python -m pytest tests/ -v
```

## Project Structure

```
py/
├── src/                 # Source code
├── tests/               # Test suite
├── config/              # Trading configuration
├── data/                # SQLite database (gitignored)
├── logs/                # Log files (gitignored)
├── bot.sh               # Shell script runner
├── daemon.py            # Python daemon wrapper
├── orchestrator.py      # Main entry point
└── Dockerfile           # Python Docker image
```
