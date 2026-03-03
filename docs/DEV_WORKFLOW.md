# C# Development Workflow

Fast iteration with volume mounts - no Docker rebuilds needed for code changes.

## Quick Start

```bash
# 1. Build base image (once)
./dev.sh build

# 2. Run with volume mounts (instant code changes)
./dev.sh run

# 3. View logs
./dev.sh logs
```

## Commands

| Command | Description |
|---------|-------------|
| `./dev.sh build` | Build base dev image |
| `./dev.sh run` | Run bot with volume mounts |
| `./dev.sh shell` | Start container with shell access |
| `./dev.sh test` | Run unit tests |
| `./dev.sh logs` | Show container logs |
| `./dev.sh stop` | Stop dev container |
| `./dev.sh clean` | Remove container and volumes |

## How It Works

The dev workflow uses Docker volume mounts to map your local source code into the container:

```
Host: ./cs/src/  →  Container: /src/src (read-only)
```

**Benefits:**
- Edit code locally with your IDE
- Changes are immediately visible in container
- No rebuild needed for code changes
- 10x faster iteration than `docker build`

**Limitations:**
- Requires rebuild if you add new files (csproj changes)
- Best for code edits, not structural changes

## Docker Compose Alternative

```bash
# Build and run
docker-compose -f docker-compose.dev.yml up --build

# Just run (after initial build)
docker-compose -f docker-compose.dev.yml up
```

## Debugging

```bash
# Get shell access
./dev.sh shell

# Inside container:
# - Check logs: cat /app/logs/*.log
# - View config: cat /app/appsettings.json
# - Database: sqlite3 /app/data/trading.db
```

## Production Build

When ready for production:

```bash
# Full build (no volume mounts)
docker build -t alpaca-fleece-cs:prod -f cs/Dockerfile ./cs
```
