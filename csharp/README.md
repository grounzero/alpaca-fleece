# AlpacaFleece - C# Implementation (Phase 1)

This is the Phase 1 (Infrastructure) C# implementation of the alpaca-fleece trading bot for Alpaca Markets.

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

### Phase 1 (Infrastructure - COMPLETE)

1. **Event Bus** (dual-channel architecture)
   - Bounded normal channel: 10,000 event capacity with DropWrite overflow
   - Unbounded exit signal channel: never dropped
   - Priority dispatch: exit signals processed first

2. **Broker Service**
   - Clock: always fresh (never cached)
   - Account/Positions: 1s TTL cache with SemaphoreSlim guard
   - Orders: no retry on writes (fatal errors)
   - Reads: ready for Polly retry policy (Phase 2)

3. **State Repository** (SQLite)
   - Key-value store (bot_state table)
   - Order intent persistence (crash recovery)
   - **Atomic gate logic**: Serializable isolation, same-bar deduplication, cooldown enforcement
   - Circuit breaker state tracking
   - Fill idempotency via unique constraint

4. **Strategy**
   - Multi-timeframe SMA crossover (5, 10, 20-period SMAs)
   - ATR calculation (14-period)
   - Regime detection: Trending Up/Down/Ranging
   - Buy/Sell signal generation

5. **Order Manager**
   - **SHA-256 deterministic client_order_id**: input = `strategy:symbol:timeframe:signalTs:side`
   - Persist-before-submit for crash recovery
   - Duplicate detection via idempotent client ID
   - Exit order support

6. **Configuration**
   - Trading symbols and risk limits
   - SMA parameters, exit rules
   - Execution options (dry run, kill switch)
   - Session and filter settings

### Phase 2-6 (Stubs)

- StreamPollerService
- StrategyService
- RiskManagerService
- OrderManagerService
- ExitManagerService
- ReconciliationService
- HousekeepingService

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

# (Phase 2) Create database migration
dotnet ef migrations add InitialCreate -p src/AlpacaFleece.Infrastructure -s src/AlpacaFleece.Worker

# Run the bot (after Phase 2 completion)
dotnet run --project src/AlpacaFleece.Worker
```

## Configuration

Edit `src/AlpacaFleece.Worker/appsettings.json`:

```json
{
  "Broker": {
    "ApiKey": "${ALPACA_API_KEY}",
    "SecretKey": "${ALPACA_SECRET_KEY}",
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

## Test Coverage (Phase 1)

- **OrderStateTests**: 11 state mappings, terminal detection, partial-terminal detection
- **EventBusTests**: normal/exit event publishing, drop on full, dispatch priority
- **StateRepositoryTests**: gate atomicity, KV crud, circuit breaker
- **OrderManagerTests**: SHA-256 idempotency, persist-before-submit, deduplication
- **BrokerServiceTests**: clock never cached, account/positions cache, kill switch
- **StrategyTests**: SMA calculation, ATR, regime detection, required history

## Next Steps (Phase 2-6)

1. **Phase 2**: WebSocket stream polling, market data handler
2. **Phase 3**: Risk manager (3-tier), order manager wire-up, position tracking
3. **Phase 4**: Exit manager (stop loss, trailing stop, profit target)
4. **Phase 5**: Nightly reconciliation, Alpaca order syncing
5. **Phase 6**: Housekeeping (state cleanup), daily reset

## Dual-Gate Protection

Live trading requires BOTH conditions:
1. `BrokerOptions.IsPaperTrading == false`
2. `BrokerOptions.AllowLiveTrading == true`

Default: Paper trading only (safe for development).

## Notes

- Database path: `trading.db` (created in application directory)
- All database operations use context managers (no connection leaks)
- Serializable isolation level for gate atomicity
- Circuit breaker: exponential backoff capped at 300 seconds

## References

- [Alpaca Markets API](https://alpaca.markets/docs)
- [Entity Framework Core](https://docs.microsoft.com/ef/)
- [Serilog](https://serilog.net/)
- [xUnit](https://xunit.net/)
- [NSubstitute](https://nsubstitute.github.io/)
