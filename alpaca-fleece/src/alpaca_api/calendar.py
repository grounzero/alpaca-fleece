"""Calendar endpoint: /v2/calendar (informational only).

IMPORTANT: Calendar is NOT used for trading gates.
/v2/clock (in broker.py) is the ONLY authoritative source for trading hours.

This module is for:
- Getting market holidays
- Getting early close times
- Informational display

NOT for:
- Deciding whether to trade
- Substituting for clock gating
"""

from datetime import datetime
from alpaca.trading.client import TradingClient

from .base import AlpacaDataClientError


class CalendarClient:
    """Fetch market calendar (holidays, early closes)."""
    
    def __init__(self, api_key: str, secret_key: str) -> None:
        """Initialise calendar client."""
        self.client = TradingClient(
            api_key=api_key,
            secret_key=secret_key,
        )
    
    def get_calendar(
        self,
        start: datetime,
        end: datetime,
    ) -> list[dict]:
        """Fetch market calendar for date range.
        
        Returns:
            List of dicts with keys: date, open, close (in America/New_York time)
        """
        try:
            calendar = self.client.get_calendar(start=start, end=end)
            return [
                {
                    "date": c.date.isoformat(),
                    "open": c.open.isoformat() if c.open else None,
                    "close": c.close.isoformat() if c.close else None,
                }
                for c in calendar
            ]
        except Exception as e:
            raise AlpacaDataClientError(f"Failed to get calendar: {e}")
