# Alpaca Fleece Trading Bot

Alpaca Fleece is an event-driven trading bot for Alpaca Markets, with both Python and C# implementations.

## Repository Layout

- Python bot: [py/README.md](py/README.md)
- C# worker: [cs/README.md](cs/README.md)
- Shared docs: [docs/](docs/), [DEPLOYMENT.md](DEPLOYMENT.md), [QUICK_START.md](QUICK_START.md), [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

## Quick Start

### Python (recommended for research and strategy iteration)

```bash
cd py
uv sync --frozen
cp .env.example .env
# Edit .env with your Alpaca API credentials
.venv/bin/python orchestrator.py
```

### C# (production-oriented worker)

```bash
cd cs
dotnet restore
dotnet build
dotnet run --project src/AlpacaFleece.Worker
```

### Docker

```bash
docker-compose build
docker-compose up -d
```

## Notes

- Python runtime files (config, data, logs) live under `py/`.
- C# runtime uses `cs/` and its own configuration defaults.
- See the per-language READMEs for detailed setup and operations.
