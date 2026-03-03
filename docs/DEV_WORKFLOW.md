# C# Development Workflow

## Quick Start

```bash
# 1. Build base image
./dev.sh build

# 2. Run bot
./dev.sh run

# 3. View logs
./dev.sh logs
```

## Commands

| Command | Description |
|---------|-------------|
| `./dev.sh build` | Build dev image |
| `./dev.sh run` | Run bot (uses pre-built DLL) |
| `./dev.sh shell` | Start container with shell access |
| `./dev.sh test` | Run unit tests |
| `./dev.sh logs` | Show container logs |
| `./dev.sh stop` | Stop container |
| `./dev.sh clean` | Remove container and volumes |

## Important: Code Changes Require Rebuild

The container runs a **pre-built DLL** (`/app/AlpacaFleece.Worker.dll`).

**After editing C# source code, you must rebuild:**
```bash
./dev.sh build && ./dev.sh run
```

**Why?** Volume mounts don't affect the compiled binary. The DLL is built at image creation time.

## Fast Iteration Options

### Option 1: Local Build (Fastest)
```bash
# Build locally (no Docker)
cd cs/src
dotnet build AlpacaFleece.Worker/AlpacaFleece.Worker.csproj

# Or with watch for auto-rebuild on save
dotnet watch --project AlpacaFleece.Worker run
```

### Option 2: Shell Access (Runtime Container)
```bash
./dev.sh shell

# Inside container (runtime only, no SDK):
# - Check logs: cat /app/logs/*.log
# - View config: cat /app/appsettings.json
# - Database: sqlite3 /app/data/trading.db
# Note: dotnet build/run won't work (SDK not included)
```

### Option 3: Volume Mounts (Config, Data, Logs, Source Tree)
The volume mounts in `./dev.sh run` are useful for:
- SQLite database persistence (`/app/data`)
- Log files (`/app/logs`)
- Runtime config changes (appsettings)
- C# source tree convenience mount (`./cs/src` → `/src`) for browsing/building inside the container

**Important:** Even though the source tree is mounted, the container entrypoint still runs the pre-built DLL baked into the image. Editing source code locally will not change the running bot until you rebuild the image (e.g., `./dev.sh build && ./dev.sh run`).
## Docker Compose Alternative

```bash
# Build and run
docker-compose -f docker-compose.dev.yml up --build

# Rebuild after code changes
docker-compose -f docker-compose.dev.yml up --build
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

## Testing

```bash
# Run tests locally (fastest)
cd cs/src
dotnet test AlpacaFleece.Tests/AlpacaFleece.Tests.csproj

# Or in container
./dev.sh test
```

## Production Build

```bash
# Full build (no volume mounts)
docker build -t alpaca-fleece-cs:prod -f cs/Dockerfile ./cs
```
