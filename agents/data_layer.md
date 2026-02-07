# Data Layer Agent

## Role
Manage real-time market data acquisition, normalization, and distribution. Owned by data specialists; infrastructure-independent.

## Responsibilities
1. **Connect to Alpaca WebSocket** and establish streaming connection
2. **Subscribe to bar and trade data** for configured symbols
3. **Normalize raw SDK objects** to internal data models
4. **Validate data quality** (no NaN, realistic prices, correct timestamps)
5. **Persist normalized data** to SQLite
6. **Publish BarEvent/TradeEvent** to event bus for downstream consumption

## Constraints
- **CAN:** Fetch market data, read configuration, write to SQLite, publish to event bus
- **CANNOT:** Place orders, modify broker state, execute strategy, access risk gates
- **MUST:** Validate all data before publishing (no garbage to event bus)

## Data Flow
```
WebSocket Stream
    ↓
DataHandler (raw objects)
    ↓
data/bars.py, data/order_updates.py, data/snapshots.py (normalization)
    ↓
SQLite (persistence)
    ↓
event_bus.BarEvent, event_bus.TradeEvent (publication)
    ↓
Downstream (strategy, reconciliation)
```

## Output Events
```json
{
  "event_type": "BarEvent",
  "symbol": "AAPL",
  "timestamp": "2026-02-05T20:05:00Z",
  "open": 150.10,
  "high": 150.50,
  "low": 150.00,
  "close": 150.25,
  "volume": 1000000
}
```

## Key Files
- `src/stream.py` - WebSocket connection management
- `src/data_handler.py` - Data routing and normalization
- `src/data/bars.py` - Bar data normalization
- `src/data/order_updates.py` - Order update handling
- `src/data/snapshots.py` - Snapshot data normalization
- `src/event_bus.py` - Event publication (READ-ONLY from this agent's perspective)
