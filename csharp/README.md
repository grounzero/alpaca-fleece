# AlpacaFleece - C# Implementation

This repository contains the C# implementation of the Alpaca Fleece trading bot for Alpaca Markets. The C# port provides a full-featured, production-oriented runtime including event routing, persistence, market-data polling, strategy wiring, risk controls, order lifecycle management and housekeeping.

## Architecture

The solution is organized into 5 projects:

1. **AlpacaFleece.Core**: Events, models, interfaces, exceptions
2. **AlpacaFleece.Infrastructure**: EF Core DbContext, repositories, broker service, event bus
3. **AlpacaFleece.Trading**: Strategy, risk manager, order manager, position tracker, configuration
4. **AlpacaFleece.Worker**: Worker host, hosted services, DI setup, appsettings
5. **AlpacaFleece.Tests**: xUnit tests with NSubstitute

## Technology Stack

- **.NET 10** with C# 13
- **Entity Framework Core** (SQLite) for persistence
- **Serilog** for structured logging
- **xUnit** for testing
- **NSubstitute** for mocking
- **Polly** for retry policies

## Project Structure

```
csharp/
├── AlpacaFleece.sln
├── src/
│   ├── AlpacaFleece.Core/
│   │   ├── Events/
│   │   ├── Models/
│   │   ├── Interfaces/
│   │   └── Exceptions/
│   ├── AlpacaFleece.Infrastructure/
│   │   ├── Broker/
│   │   ├── Data/
│   │   ├── Migrations/
│   │   ├── Repositories/
│   │   └── EventBus/
│   ├── AlpacaFleece.Trading/
│   │   ├── Config/
│   │   ├── Strategy/
│   │   ├── Risk/
│   │   ├── Orders/
│   │   └── Positions/
│   └── AlpacaFleece.Worker/
│       ├── Services/
│       └── Properties/
└── tests/
    └── AlpacaFleece.Tests/
```

## Key Features

## Implemented features

- Event routing: dual-channel event bus with priority dispatch for exit signals and a bounded main channel.
- Broker integration: Alpaca broker abstraction with fresh clock reads, short-lived account/position cache, and safe order submission semantics.
- Persistence: SQLite-backed state repository including order intent persistence, idempotent fill recording, circuit-breaker state and key-value state store.
- Strategy: multi-timeframe SMA crossover strategy with ATR and regime detection.
- Order lifecycle: deterministic `client_order_id` generation (SHA-256), persist-before-submit flow, and deduplication.
- Configuration: runtime options for symbols, risk limits, exit rules, and execution gates (dry-run / kill-switch).

## Runtime services

The worker project registers and runs a set of hosted services that provide market-data polling, bar persistence, exit handling, reconciliation and housekeeping. Notable services included in the `AlpacaFleece.Worker` project:

- `StreamPollerService` — polls Alpaca for bars and order updates and publishes `BarEvent`/`OrderUpdateEvent` to the event bus.
- `BarsHandler` — persists incoming bars to SQLite and maintains an in-memory deque per symbol for fast access.
- `ExitManagerService` — runtime wrapper that executes exit logic (stop loss, trailing stop, profit target).
- `ReconciliationService` — startup and runtime reconciliation, fill reconciliation and discrepancy reporting.
- `HousekeepingService` — periodic equity snapshots, daily reset tasks and graceful shutdown flattening.

There are a few small stub services included for incremental development (see `Services/StubServices.cs`), but the core runtime pipeline is implemented and registered in `Program.cs`.

## Setup Instructions

### Prerequisites

- **.NET 10 SDK** (download from https://dotnet.microsoft.com/download)
- **SQLite** (included with .NET)

### Installation

```bash
cd /Users/me/git/alpaca-fleece/csharp

# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run tests
dotnet test

## Database migration (EF Core)
If you need to create the initial migration locally:

```bash
dotnet ef migrations add InitialCreate -p src/AlpacaFleece.Infrastructure -s src/AlpacaFleece.Worker
```
```

### Running the Bot

**Foreground (with console output):**
```bash
cd /Users/me/git/alpaca-fleece/csharp
dotnet run --project src/AlpacaFleece.Worker
```

**Background (survives terminal disconnect):**
```bash
cd /Users/me/git/alpaca-fleece/csharp
dotnet run --project src/AlpacaFleece.Worker &
```

**With environment variables:**
```bash
cd /Users/me/git/alpaca-fleece/csharp
ALPACA_API_KEY=your_key ALPACA_SECRET_KEY=your_secret dotnet run --project src/AlpacaFleece.Worker
```

The application will:
- Create `trading.db` automatically
- Initialize all database tables
- Load position tracking data
- Start the event loop (queries database every 2 seconds)
- Log to `logs/alpaca-fleece.log`

Stop with **Ctrl+C**.

## Configuration

### Alpaca API Credentials

**Required:** Valid Alpaca API credentials for the application to connect.

1. Create a free account at [Alpaca Markets](https://alpaca.markets)
2. Go to **Dashboard → API Keys** and generate new keys
3. Copy your **API Key** and **Secret Key**

**Paper Trading (default - safe for testing):**
- Use your credentials with `IsPaperTrading: true` (default)
- No real money is risked
- Perfect for development and testing

**Live Trading (requires dual gates):**
- Set `IsPaperTrading: false` AND `AllowLiveTrading: true`
- This is intentionally difficult to prevent accidental live trading

### Setting Credentials

Edit `src/AlpacaFleece.Worker/appsettings.json`:

```json
{
  "Broker": {
    "ApiKey": "YOUR_ACTUAL_API_KEY_HERE",
    "SecretKey": "YOUR_ACTUAL_SECRET_KEY_HERE",
    "BaseUrl": "https://paper-api.alpaca.markets",
    "IsPaperTrading": true,
    "AllowLiveTrading": false,
    "DryRun": false,
    "KillSwitch": false
  },
  "Trading": {
    "Symbols": { "Symbols": ["AAPL", "MSFT", "GOOG"] },
    "RiskLimits": { "MaxDailyLoss": 500 },
    "Exit": { "StopLossMultiplier": 1.5, "ProfitTargetMultiplier": 3.0 }
  }
}
```

**OR** use environment variables:
```bash
export ALPACA_API_KEY="your_api_key"
export ALPACA_SECRET_KEY="your_secret_key"
dotnet run --project src/AlpacaFleece.Worker
```

**Troubleshooting:**
- If you see "Unauthorized" errors, verify your API credentials are correct
- Ensure you're using the paper trading endpoint for testing
- Check that your Alpaca account is active and API access is enabled

## Logging

Structured logs to:
- **Console**: JSON format
- **File**: `logs/alpaca-fleece.log` (rolling daily, 30-day retention)

## Testing

Run all tests:
```bash
dotnet test
```

Run specific test class:
```bash
dotnet test --filter "ClassName=OrderStateTests"
```

With coverage:
```bash
dotnet test /p:CollectCoverage=true
```

## Code Standards

- **Low-allocation**: No LINQ in hot paths, ValueTask for async methods
- **Sealed classes**: All implementations are sealed
- **Primary constructors**: Modern C# syntax
- **Type hints**: Nullable reference types enabled, strict mypy-equivalent
- **GlobalUsings.cs**: No duplication in individual files
- **File-scoped namespaces**: All files
- **Line length**: 100 characters (black/ruff compatible)
- **Logging**: No string interpolation in logger calls

## Test coverage and CI

There are unit and integration tests covering event bus, state repository, order manager and strategy logic. Run the test suite locally with `dotnet test`.

## Notes / Safety

- Dual-gate protection prevents accidental live trading: live mode requires both `IsPaperTrading == false` and `AllowLiveTrading == true`.
- Database path: `trading.db` (created in application directory).
- Database operations use short-lived EF Core contexts and adopt idempotent patterns to avoid duplicate side effects.


## References

- [Alpaca Markets API](https://alpaca.markets/docs)
- [Entity Framework Core](https://docs.microsoft.com/ef/)
- [Serilog](https://serilog.net/)
- [xUnit](https://xunit.net/)
- [NSubstitute](https://nsubstitute.github.io/)
