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
from typing import Any

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
    ) -> list[dict[str, Any]]:
        """Fetch market calendar for date range.

        Returns:
            List of dicts with keys: date, open, close (in America/New_York time)
        """
        try:
            # get_calendar takes no arguments, returns list of Calendar objects
            calendar = self.client.get_calendar()
            return [
                {
                    "date": c.date.isoformat() if hasattr(c, "date") and c.date else "",
                    "open": c.open.isoformat() if hasattr(c, "open") and c.open else None,
                    "close": c.close.isoformat() if hasattr(c, "close") and c.close else None,
                }
                for c in calendar
            ]
        except Exception as e:
            raise AlpacaDataClientError(f"Failed to get calendar: {e}")
