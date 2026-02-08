"""Asset reference endpoints: /v2/assets, /v2/watchlists."""

from typing import Any, Union

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

    def get_assets(
        self, status: str = "active", asset_class: str = "us_equity"
    ) -> list[dict[str, Any]]:
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

            assets_result: Union[list[Any], dict[str, Any]] = self.client.get_all_assets()
            if isinstance(assets_result, dict):
                return []  # Error case

            result: list[dict[str, Any]] = []
            for a in assets_result:
                if isinstance(a, str):
                    continue  # Skip error strings
                # Asset objects have these attributes directly
                if (
                    hasattr(a, "tradable")
                    and a.tradable
                    and hasattr(a, "asset_class")
                    and a.asset_class == asset_class
                ):
                    result.append(
                        {
                            "symbol": a.symbol if hasattr(a, "symbol") else "",
                            "name": a.name if hasattr(a, "name") else "",
                            "status": str(a.status) if hasattr(a, "status") else "",
                            "tradable": a.tradable,
                            "asset_class": a.asset_class,
                        }
                    )
            return result
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
            # get_watchlist_by_name returns a Watchlist object with assets
            from alpaca.trading.models import Watchlist

            watchlists_result: Union[list[Any], dict[str, Any]] = self.client.get_watchlists()
            if isinstance(watchlists_result, dict):
                raise AlpacaDataClientError("Failed to get watchlists")

            for wl in watchlists_result:
                if isinstance(wl, str):
                    continue
                if hasattr(wl, "name") and wl.name == name:
                    # Fetch full watchlist to get assets
                    if not hasattr(wl, "id"):
                        continue
                    full_wl_result: Union[
                        Watchlist, dict[str, Any]
                    ] = self.client.get_watchlist_by_id(wl.id)
                    if isinstance(full_wl_result, dict):
                        continue
                    if hasattr(full_wl_result, "assets") and full_wl_result.assets:
                        return [a.symbol for a in full_wl_result.assets if hasattr(a, "symbol")]
                    return []
            raise AlpacaDataClientError(f"Watchlist {name} not found")
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
