"""Base client for Alpaca API.

Provides low-level access to Alpaca data endpoints including historical bars,
snapshots, and other market data. All client classes inherit from this base.
"""

from typing import Any, Optional, Type

# The CryptoHistoricalDataClient may not be available in all versions of the
# Alpaca SDK. Declare the name as an optional type for static checkers, then
# attempt to import the concrete classes at runtime.
CryptoHistoricalDataClient: Optional[Type[Any]] = None
try:
    from alpaca.data.historical import StockHistoricalDataClient

    try:
        from alpaca.data.historical import CryptoHistoricalDataClient as _CryptoHistoricalDataClient

        CryptoHistoricalDataClient = _CryptoHistoricalDataClient
    except Exception:  # pragma: no cover - optional crypto client
        CryptoHistoricalDataClient = None
except Exception:
    # If the alpaca SDK is not installed, allow the import error to surface
    # as tests will run in an environment with the dependency present.
    raise


class AlpacaDataClientError(Exception):
    """Raised when Alpaca API call fails."""

    pass


class AlpacaDataClient:
    """Base client for all Alpaca data endpoints.

    Encapsulates the StockHistoricalDataClient from the alpaca-py SDK
    and provides a common base for market data access.

    Attributes:
        api_key: Alpaca API key for authentication
        secret_key: Alpaca secret key for authentication
        client: StockHistoricalDataClient instance from alpaca-py
    """

    def __init__(
        self, api_key: str, secret_key: str, trading_config: Optional[dict[str, Any]] = None
    ) -> None:
        """Initialise data client.

        Args:
            api_key: Alpaca API key for authentication
            secret_key: Alpaca secret key for authentication

        Raises:
            ValueError: If api_key or secret_key is empty
        """
        if not api_key or not secret_key:
            raise ValueError("API key and secret key are required")

        self.api_key: str = api_key
        self.secret_key: str = secret_key
        # Optional trading configuration passed from higher-level callers
        self.trading_config: dict[str, Any] = trading_config or {}

        # Instantiate SDK clients only for the asset classes configured in
        # `trading_config['symbols']`. This allows a bot to run with only
        # crypto or only equities without unnecessarily constructing the
        # other SDK client.
        self.stock_client: Any = None
        self.crypto_client: Any = None

        try:
            symbols_cfg = (
                self.trading_config.get("symbols", {})
                if isinstance(self.trading_config, dict)
                else {}
            )
            crypto_list = (
                [str(s) for s in symbols_cfg.get("crypto_symbols", [])] if symbols_cfg else []
            )
            equity_list = (
                [str(s) for s in symbols_cfg.get("equity_symbols", [])] if symbols_cfg else []
            )
        except Exception:
            crypto_list = []
            equity_list = []

        if equity_list and StockHistoricalDataClient is not None:
            try:
                self.stock_client = StockHistoricalDataClient(
                    api_key=api_key,
                    secret_key=secret_key,
                )
            except Exception:
                self.stock_client = None

        if crypto_list and CryptoHistoricalDataClient is not None:
            try:
                self.crypto_client = CryptoHistoricalDataClient(
                    api_key=api_key, secret_key=secret_key
                )
            except Exception:
                self.crypto_client = None
