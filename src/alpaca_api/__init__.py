"""Alpaca API data clients - DATA FETCH ONLY.

This package owns all non-execution data endpoints:
- /v2/stocks/bars (market_data.py)
- /v2/stocks/snapshots (market_data.py)
- /v2/assets (assets.py)
- /v2/watchlists/{name} (assets.py)
- /v2/calendar (calendar.py)

MUST NOT:
- Submit/cancel orders
- Write to SQLite
- Publish to EventBus
- Call /v2/clock (that is broker.py's exclusive domain)
"""
