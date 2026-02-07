# Project-Specific Standards for alpaca-fleece

**AGENT-ONLY — Do not include in project files**

These standards apply specifically to work on **alpaca-fleece**.

## Project Context

- **Name:** alpaca-fleece
- **Path:** /home/t-rox/.openclaw/workspace/alpaca-fleece
- **Type:** Algorithmic trading bot
- **Broker:** Alpaca Markets (paper trading)
- **Language:** Python 3.12+

## Architecture

The bot uses a phase-based architecture:
1. **Infrastructure** — Config, broker, state store, reconciliation
2. **Data Layer** — Event bus, HTTP polling, data handlers
3. **Trading Logic** — Strategy, risk manager, order manager
4. **Runtime** — Event processing loop

## Key Technologies

- **Data:** SQLite for state persistence
- **API:** Alpaca Markets REST API
- **Async:** asyncio for concurrent operations
- **Testing:** pytest with 109+ tests

## Domain-Specific Rules

### Financial Calculations
- Use `Decimal` for all monetary values (never float)
- Round only at presentation layer
- Document all calculation assumptions
- Include unit tests for financial math

### Trading Safety
- Never disable kill switch or circuit breaker
- All orders must have deterministic `client_order_id`
- Persist order intent to SQLite BEFORE submission
- Fresh `/v2/clock` call before every order (market hours check)
- Respect daily loss limits and position sizing

### Database Patterns
- Use `NUMERIC` type for financial values (not REAL)
- Always use context managers: `with sqlite3.connect() as conn:`
- Migrations: Add new tables/columns, never modify existing in place

### State Management
- Circuit breaker count persisted across restarts
- Daily P&L tracked in `bot_state` table
- Last signals per symbol stored to prevent duplicates
- Automatic daily state reset at market open

## Code Locations

- Source: `/home/t-rox/.openclaw/workspace/alpaca-fleece/src/`
- Tests: `/home/t-rox/.openclaw/workspace/alpaca-fleece/tests/`
- Config: `/home/t-rox/.openclaw/workspace/alpaca-fleece/config/trading.yaml`
- Data: `/home/t-rox/.openclaw/workspace/alpaca-fleece/data/trades.db`
- Logs: `/home/t-rox/.openclaw/workspace/alpaca-fleece/logs/`

## Testing Requirements

- All new features must have tests
- Target 80%+ coverage (90%+ for critical paths)
- Mock Alpaca API in tests
- Run full suite: `/home/t-rox/.openclaw/workspace/alpaca-fleece/.venv/bin/python -m pytest tests/ -v`

## Verification Checklist

Before completing any task:
- [ ] Code follows general Python standards (see CODING_STANDARDS.md)
- [ ] Project-specific rules above are followed
- [ ] Tests written and passing
- [ ] No hardcoded paths (use relative or env vars)
- [ ] No secrets in code
- [ ] British English in comments and docstrings
- [ ] Safety checks (kill switch, circuit breaker) not disabled
