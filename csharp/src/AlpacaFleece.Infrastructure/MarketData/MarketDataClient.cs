using Alpaca.Markets;
using AlpacaFleece.Infrastructure.Broker;

namespace AlpacaFleece.Infrastructure.MarketData;

/// <summary>
/// Wrapper for Alpaca market data API.
/// Routes equity bars/quotes to IAlpacaDataClient and crypto to IAlpacaCryptoDataClient.
/// Automatically uses IEX feed for paper trading (SIP requires paid subscription).
/// </summary>
public sealed class MarketDataClient(
    IAlpacaDataClient equityDataClient,
    IAlpacaCryptoDataClient cryptoDataClient,
    BrokerOptions brokerOptions,
    ILogger<MarketDataClient> logger) : IMarketDataClient
{
    private const int RequestTimeoutMs = 10000;

    /// <summary>
    /// Detects if symbol is equity (not crypto).
    /// Crypto symbols contain '/' (e.g., "BTC/USD").
    /// </summary>
    public bool IsEquity(string symbol) => !symbol.Contains('/');

    /// <summary>
    /// Detects if symbol is crypto.
    /// </summary>
    public bool IsCrypto(string symbol) => symbol.Contains('/');

    /// <summary>
    /// Normalises Alpaca IBar to internal Quote record.
    /// </summary>
    public Quote NormalizeQuote(
        string symbol,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        DateTimeOffset timestamp) =>
        new(symbol, timestamp, open, high, low, close, volume);

    /// <summary>
    /// Fetches the most recent <paramref name="limit"/> bars for a symbol.
    /// Detects equity vs crypto automatically. Returns bars in ascending chronological order.
    /// Uses IEX feed for paper trading to avoid SIP subscription errors.
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
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol cannot be empty", nameof(symbol));

        if (limit < 1 || limit > 10000)
            throw new ArgumentException("Limit must be between 1 and 10000", nameof(limit));

        try
        {
            // Validate symbol format (invalid chars like @ cause API errors); wrapped as MarketDataException.
            if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, @"^[A-Za-z0-9/\-\.]+$"))
                throw new InvalidOperationException($"Symbol '{symbol}' contains invalid characters");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeoutMs);

            var timeFrame = MapTimeFrame(timeframe);
            var into = DateTime.UtcNow;

            IReadOnlyList<IBar> items;

            if (IsEquity(symbol))
            {
                // 5 calendar days covers any weekend/holiday gap; page size 1000 fits
                // up to ~780 bars (2 full trading days × 390 min/day) in one request.
                var from = into.AddDays(-5);
                var request = new HistoricalBarsRequest(symbol, from, into, timeFrame)
                    .WithPageSize(1000);

                // Use IEX feed for paper trading (SIP requires paid subscription)
                if (brokerOptions.IsPaperTrading)
                {
                    request.Feed = MarketDataFeed.Iex;
                    logger.LogDebug("Using IEX feed for {Symbol} (paper trading)", symbol);
                }
                var page = await equityDataClient.ListHistoricalBarsAsync(request, cts.Token);
                items = page.Items;
            }
            else
            {
                // Crypto trades 24/7; limit×2 minutes back is always sufficient.
                var from = into.AddMinutes(-(limit * 2 + 30));
                var request = new HistoricalCryptoBarsRequest(symbol, from, into, timeFrame)
                    .WithPageSize((uint)(limit * 2 + 30));
                var page = await cryptoDataClient.ListHistoricalBarsAsync(request, cts.Token);
                items = page.Items;
            }

            // Bars are returned ascending (oldest first); take the tail to get the most recent limit.
            var bars = items
                .TakeLast(limit)
                .Select(bar => NormalizeQuote(
                    symbol,
                    bar.Open,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    (long)bar.Volume,
                    new DateTimeOffset(bar.TimeUtc, TimeSpan.Zero)))
                .ToList()
                .AsReadOnly();

            logger.LogDebug(
                "Fetched {Count} bars for {Symbol} at {Timeframe} (requested {Limit})",
                bars.Count, symbol, timeframe, limit);

            return bars;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("GetBars cancelled for {Symbol}", symbol);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch bars for {Symbol}", symbol);
            throw new MarketDataException($"Failed to fetch bars for {symbol}", ex);
        }
    }

    /// <summary>
    /// Fetches bid/ask snapshot for a symbol. Detects equity vs crypto automatically.
    /// </summary>
    public ValueTask<BidAskSpread> GetSnapshotAsync(
        string symbol,
        CancellationToken ct = default)
    {
        return GetSnapshotAsyncImpl(symbol, ct);
    }

    private ValueTask<BidAskSpread> GetSnapshotAsyncImpl(string symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol cannot be empty", nameof(symbol));

        // Note: Snapshot API methods have been deprecated in Alpaca.Markets 7.2.0.
        // Recommend using ListSnapshotsAsync or other alternatives.
        // For now, throw NotImplementedException until snapshot APIs are fully updated.
        throw new NotImplementedException(
            $"GetSnapshot is not yet implemented for {symbol}. " +
            "The Alpaca.Markets SDK has deprecated snapshot methods. " +
            "Please use bar history data or update to use current snapshot APIs.");
    }

    /// <summary>
    /// Maps string timeframe to Alpaca BarTimeFrame.
    /// </summary>
    private static BarTimeFrame MapTimeFrame(string timeframe) =>
        timeframe.ToUpperInvariant() switch
        {
            "1MIN" or "1M" => BarTimeFrame.Minute,
            "5MIN" or "5M" => new BarTimeFrame(5, BarTimeFrameUnit.Minute),
            "15MIN" or "15M" => new BarTimeFrame(15, BarTimeFrameUnit.Minute),
            "1H" or "1HOUR" => BarTimeFrame.Hour,
            "1D" or "1DAY" => BarTimeFrame.Day,
            _ => BarTimeFrame.Minute
        };
}

/// <summary>
/// Exception thrown when market data operations fail.
/// </summary>
public sealed class MarketDataException : Exception
{
    public MarketDataException(string message) : base(message) { }
    public MarketDataException(string message, Exception innerException) : base(message, innerException) { }
}
