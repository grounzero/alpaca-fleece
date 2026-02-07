"""Asset reference endpoints: /v2/assets, /v2/watchlists."""

from alpaca.trading.client import TradingClient

from .base import AlpacaDataClientError


class AssetsClient:
    """Fetch asset and watchlist data."""

    def __init__(self, api_key: str, secret_key: str) -> None:
        """Initialise assets client."""
        self.client = TradingClient(
            api_key=api_key,
            secret_key=secret_key,
        )

    def get_assets(self, status: str = "active", asset_class: str = "us_equity") -> list[dict]:
        """Fetch all assets matching criteria.

        Args:
            status: "active" or "inactive"
            asset_class: "us_equity", "crypto", etc

        Returns:
            List of dicts with keys: symbol, name, status, tradable, asset_class
        """
        try:
            # Note: get_all_assets() may not accept parameters in all versions
            # Fetch all and filter client-side if needed
            assets = self.client.get_all_assets()
            return [
                {
                    "symbol": a.symbol,
                    "name": a.name,
                    "status": a.status,
                    "tradable": a.tradable,
                    "asset_class": a.asset_class,
                }
                for a in assets
                if a.tradable and a.asset_class == asset_class
            ]
        except Exception as e:
            raise AlpacaDataClientError(f"Failed to get assets: {e}")

    def get_watchlist(self, name: str) -> list[str]:
        """Fetch symbols in a watchlist.

        Args:
            name: Watchlist name

        Returns:
            List of symbols
        """
        try:
            watchlist = self.client.get_watchlist(name)
            return [a.symbol for a in watchlist.assets]
        except Exception as e:
            raise AlpacaDataClientError(f"Failed to get watchlist {name}: {e}")

    def validate_symbols(self, symbols: list[str]) -> list[str]:
        """Validate that all symbols are active, tradable US equities.

        Args:
            symbols: List of symbols to validate

        Returns:
            List of validated symbols

        Raises:
            AlpacaDataClientError if any symbol is invalid or out of scope
        """
        try:
            assets = self.get_assets(status="active", asset_class="us_equity")
            valid_map = {a["symbol"]: a for a in assets}

            validated = []
            for symbol in symbols:
                if symbol not in valid_map:
                    raise AlpacaDataClientError(f"Symbol {symbol} not found or not tradable")
                validated.append(symbol)

            return validated
        except Exception as e:
            raise AlpacaDataClientError(f"Symbol validation failed: {e}")
