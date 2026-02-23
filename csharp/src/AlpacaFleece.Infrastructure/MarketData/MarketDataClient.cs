using Alpaca.Markets;
using AlpacaFleece.Infrastructure.Broker;

namespace AlpacaFleece.Infrastructure.MarketData;

/// <summary>
/// Wrapper for Alpaca market data API.
/// Routes equity bars/quotes to IAlpacaDataClient and crypto to IAlpacaCryptoDataClient.
/// </summary>
public sealed class MarketDataClient(
    IAlpacaDataClient equityDataClient,
    IAlpacaCryptoDataClient cryptoDataClient,
    ILogger<MarketDataClient> logger) : IMarketDataClient
{
    private const int RequestTimeoutMs = 10000;

    /// <summary>
    /// Fetches the most recent <paramref name="limit"/> bars for a symbol.
    /// Detects equity vs crypto automatically. Returns bars in ascending chronological order.
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

    private async ValueTask<BidAskSpread> GetSnapshotAsyncImpl(string symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol cannot be empty", nameof(symbol));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeoutMs);

            IQuote? quote;
            if (IsEquity(symbol))
            {
                quote = await equityDataClient.GetLatestQuoteAsync(
                    new LatestMarketDataRequest(symbol), cts.Token);
            }
            else
            {
                // Single-symbol crypto methods are obsolete in v7; use the list variant.
                var quotes = await cryptoDataClient.ListLatestQuotesAsync(
                    new LatestDataListRequest(new[] { symbol }), cts.Token);
                quotes.TryGetValue(symbol, out quote);
            }

            if (quote is null)
            {
                logger.LogWarning("No quote returned for {Symbol}; returning zero spread", symbol);
                return new BidAskSpread(Symbol: symbol, Bid: 0m, Ask: 0m,
                    BidSize: 0, AskSize: 0, FetchedAt: DateTimeOffset.UtcNow);
            }

            var spread = new BidAskSpread(
                Symbol: symbol,
                Bid: quote.BidPrice,
                Ask: quote.AskPrice,
                BidSize: (long)quote.BidSize,
                AskSize: (long)quote.AskSize,
                FetchedAt: DateTimeOffset.UtcNow);

            logger.LogDebug(
                "Fetched snapshot for {Symbol}: bid={Bid} ask={Ask} spread={Spread:P2}",
                symbol, spread.Bid, spread.Ask, spread.SpreadPercent / 100m);

            return spread;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("GetSnapshot cancelled for {Symbol}", symbol);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch snapshot for {Symbol}", symbol);
            throw new MarketDataException($"Failed to fetch snapshot for {symbol}", ex);
        }
    }

    /// <summary>
    /// Detects if a symbol is equity (not crypto).
    /// </summary>
    public bool IsEquity(string symbol)
    {
        if (symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ||
            symbol.EndsWith("USD", StringComparison.OrdinalIgnoreCase))
            return false;

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
    /// Normalises quote from Alpaca SDK format to internal Quote format.
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
        if (high < low)
            logger.LogWarning("Invalid quote for {Symbol}: high < low", symbol);

        if (close < 0 || open < 0)
            logger.LogWarning("Invalid quote for {Symbol}: negative close/open", symbol);

        if (volume < 0)
            logger.LogWarning("Invalid quote for {Symbol}: negative volume", symbol);

        return new Quote(
            Symbol: symbol,
            Date: timestamp.DateTime,
            Open: open,
            High: high,
            Low: low,
            Close: close,
            Volume: volume);
    }

    /// <summary>
    /// Maps timeframe string (e.g. "1Min") to Alpaca SDK BarTimeFrame.
    /// </summary>
    private static BarTimeFrame MapTimeFrame(string timeframe) =>
        timeframe.ToUpperInvariant() switch
        {
            "1MIN" or "1MINUTE" or "MINUTE" => BarTimeFrame.Minute,
            "5MIN" or "5MINUTE"             => new BarTimeFrame(5, BarTimeFrameUnit.Minute),
            "15MIN" or "15MINUTE"           => new BarTimeFrame(15, BarTimeFrameUnit.Minute),
            "30MIN" or "30MINUTE"           => new BarTimeFrame(30, BarTimeFrameUnit.Minute),
            "1HOUR" or "1H" or "HOUR"       => BarTimeFrame.Hour,
            "1DAY" or "1D" or "DAY"         => BarTimeFrame.Day,
            _                               => BarTimeFrame.Minute,
        };
}

/// <summary>
/// Market data specific exception.
/// </summary>
public sealed class MarketDataException(string message, Exception? innerException = null)
    : Exception(message, innerException);
