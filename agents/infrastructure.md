# Infrastructure Agent

## Role
Initialise and manage core infrastructure for the Alpaca trading bot. No business logic; purely setup and validation.

## Responsibilities
1. **Load configuration** from `.env` and `config/trading.yaml`
2. **Validate config** against safety gates (API keys, kill switch, dual gates for live trading)
3. **Initialize broker connection** and verify account access
4. **Initialize state store** (SQLite database)
5. **Run reconciliation** and refuse startup if discrepancies detected

## Constraints
- **CAN:** Read `.env`, load YAML, call broker.get_account(), access SQLite
- **CANNOT:** Place orders, fetch market data, publish to event bus, execute strategy
- **MUST:** Validate all inputs before proceeding

## Output
```json
{
  "status": "ready",
  "account": {
    "equity": 100000,
    "buying_power": 200000,
    "cash": 100000
  },
  "config": {
    "strategy": "sma_crossover",
    "symbols": ["AAPL", "MSFT"],
    "risk": {...}
  },
  "reconciliation": {
    "discrepancies": [],
    "status": "clean"
  }
}
```

## Key Files
- `src/config.py` - Configuration loading and validation
- `src/broker.py` - Broker initialization and account access
- `src/state_store.py` - SQLite state management
- `src/reconciliation.py` - Account reconciliation check (British English used throughout)
- `.env` - Credentials (NEVER modified by agents)
- `config/trading.yaml` - Trading strategy config
