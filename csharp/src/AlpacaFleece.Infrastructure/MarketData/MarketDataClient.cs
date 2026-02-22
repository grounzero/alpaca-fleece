namespace AlpacaFleece.Infrastructure.MarketData;

/// <summary>
/// Wrapper for Alpaca market data API.
/// Provides bar history and snapshot data with quote normalization.
/// </summary>
public sealed class MarketDataClient(ILogger<MarketDataClient> logger) : IMarketDataClient
{
    private const int RequestTimeoutMs = 10000;

    /// <summary>
    /// Fetches bars for a symbol (stock or crypto detected automatically).
    /// Returns IReadOnlyList of quotes with OHLCV data.
    /// </summary>
    public ValueTask<IReadOnlyList<Quote>> GetBarsAsync(
        string symbol,
        string timeframe,
        int limit,
        CancellationToken ct = default)
    {
        return GetBarsAsyncImpl(symbol, timeframe, limit, ct);
    }

    private async ValueTask<IReadOnlyList<Quote>> GetBarsAsyncImpl(
        string symbol,
        string timeframe,
        int limit,
        CancellationToken ct)
    {
        // Validate inputs - these throw ArgumentException and are not caught
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol cannot be empty", nameof(symbol));

        if (limit < 1 || limit > 10000)
            throw new ArgumentException("Limit must be between 1 and 10000", nameof(limit));

        try
        {
            // Validate symbol format (invalid chars like @ cause API errors)
            if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, @"^[A-Za-z0-9/\-\.]+$"))
                throw new InvalidOperationException($"Symbol '{symbol}' contains invalid characters");

            // Placeholder: In production, this will call Alpaca REST API
            // For now, return empty list to prevent errors in Phase 2 development
            var quotes = new List<Quote>();

            logger.LogDebug(
                "Fetched {count} bars for {symbol} at {timeframe}",
                quotes.Count,
                symbol,
                timeframe);

            return quotes.AsReadOnly();
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("GetBars cancelled for {symbol}", symbol);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch bars for {symbol}", symbol);
            throw new MarketDataException($"Failed to fetch bars for {symbol}", ex);
        }
    }

    /// <summary>
    /// Fetches bid/ask snapshot for a symbol.
    /// Detects equity vs crypto and returns BidAskSpread.
    /// </summary>
    public ValueTask<BidAskSpread> GetSnapshotAsync(
        string symbol,
        CancellationToken ct = default)
    {
        return GetSnapshotAsyncImpl(symbol, ct);
    }

    private async ValueTask<BidAskSpread> GetSnapshotAsyncImpl(
        string symbol,
        CancellationToken ct)
    {
        // Validate inputs - these throw ArgumentException and are not caught
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol cannot be empty", nameof(symbol));

        try
        {
            // Placeholder: Will call actual Alpaca snapshot API
            var spread = new BidAskSpread(
                Symbol: symbol,
                Bid: 0m,
                Ask: 0m,
                BidSize: 0,
                AskSize: 0,
                FetchedAt: DateTimeOffset.UtcNow);

            logger.LogDebug("Fetched snapshot for {symbol}: bid={bid} ask={ask}",
                symbol, spread.Bid, spread.Ask);

            return spread;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("GetSnapshot cancelled for {symbol}", symbol);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch snapshot for {symbol}", symbol);
            throw new MarketDataException($"Failed to fetch snapshot for {symbol}", ex);
        }
    }

    /// <summary>
    /// Detects if a symbol is equity (NYSE/NASDAQ) or crypto.
    /// </summary>
    public bool IsEquity(string symbol)
    {
        // Crypto symbols typically end with USDT, USD, or are short uppercase
        if (symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ||
            symbol.EndsWith("USD", StringComparison.OrdinalIgnoreCase))
            return false;

        // Check for known crypto patterns
        var cryptoPatterns = new[] { "BTC", "ETH", "XRP", "ADA", "DOGE", "SHIB" };
        if (cryptoPatterns.Any(p => symbol.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    /// <summary>
    /// Detects if a symbol is crypto.
    /// </summary>
    public bool IsCrypto(string symbol) => !IsEquity(symbol);

    /// <summary>
    /// Normalizes quote from Alpaca SDK format to Skender Quote format.
    /// </summary>
    public Quote NormalizeQuote(
        string symbol,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        DateTimeOffset timestamp)
    {
        // Validate OHLC
        if (high < low)
            logger.LogWarning("Invalid quote for {symbol}: high < low", symbol);

        if (close < 0 || open < 0)
            logger.LogWarning("Invalid quote for {symbol}: negative close/open", symbol);

        if (volume < 0)
            logger.LogWarning("Invalid quote for {symbol}: negative volume", symbol);

        return new Quote(
            Symbol: symbol,
            Date: timestamp.DateTime,
            Open: open,
            High: high,
            Low: low,
            Close: close,
            Volume: volume);
    }
}

/// <summary>
/// Market data specific exceptions.
/// </summary>
public sealed class MarketDataException(string message, Exception? innerException = null)
    : Exception(message, innerException);

