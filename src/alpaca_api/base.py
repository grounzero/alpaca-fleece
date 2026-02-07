"""Base client for Alpaca API.

Provides low-level access to Alpaca data endpoints including historical bars,
snapshots, and other market data. All client classes inherit from this base.
"""

from alpaca.data.historical import StockHistoricalDataClient


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
    
    def __init__(self, api_key: str, secret_key: str) -> None:
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
        self.client: StockHistoricalDataClient = StockHistoricalDataClient(
            api_key=api_key,
            secret_key=secret_key,
        )
