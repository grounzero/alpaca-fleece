using Alpaca.Markets;
using AlpacaFleece.Core.Interfaces;
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
    ILogger<MarketDataClient> logger,
    ISymbolClassifier symbolClassifier,
    int maxPriceAgeSeconds = 300) : IMarketDataClient
{
    private const int RequestTimeoutMs = 10000;
    private readonly int _maxPriceAgeSeconds = maxPriceAgeSeconds;

    /// <summary>
    /// Detects if symbol is equity (not crypto).
    /// Delegates to configured `ISymbolClassifier`.
    /// </summary>
    public bool IsEquity(string symbol) => symbolClassifier.IsEquity(symbol);
    /// <summary>
    /// Detects if symbol is crypto.
    /// Delegates to configured `ISymbolClassifier`.
    /// </summary>
    public bool IsCrypto(string symbol) => symbolClassifier.IsCrypto(symbol);

    /// <summary>
    /// Converts Alpaca IBar to internal Quote record.
    /// </summary>
    public Quote CreateQuote(
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

            // Ensure symbol is classified to prevent silently routing to wrong API endpoint
            var isEquity = IsEquity(symbol);
            var isCrypto = IsCrypto(symbol);
            
            if (!isEquity && !isCrypto)
            {
                throw new InvalidOperationException(
                    $"Symbol '{symbol}' is not classified as equity or crypto. " +
                    "Add it to the appropriate symbol list in configuration.");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeoutMs);

            var timeFrame = MapTimeFrame(timeframe);
            var into = DateTime.UtcNow;

            IReadOnlyList<IBar> items;

            if (isEquity)
            {
                // Window and page size are derived from the already-mapped BarTimeFrame so the
                // unit is always correct regardless of input format ("1m", "1MIN", etc.).
                // Page size is clamped to the API maximum of 10 000.
                static int MinuteWindowDays(int limit, int barMinutes)
                {
                    const double tradingMinutesPerDay = 390.0; // US equity regular session
                    var barsPerDay = tradingMinutesPerDay / Math.Max(1, barMinutes);
                    var daysNeeded = (int)Math.Ceiling(limit / barsPerDay) + 2; // safety margin
                    return Math.Max(daysNeeded, 5); // never less than 5 days
                }

                var from = timeFrame.Unit switch
                {
                    BarTimeFrameUnit.Day    => into.AddDays(-(limit * 7 / 5 + 5)),
                    BarTimeFrameUnit.Hour   => into.AddHours(-(limit + 5)),
                    BarTimeFrameUnit.Minute => into.AddDays(-MinuteWindowDays(limit, timeFrame.Value)),
                    _                      => into.AddDays(-5)
                };
                var rawPageSize = timeFrame.Unit switch
                {
                    BarTimeFrameUnit.Day    => limit * 7 / 5 + 5,
                    BarTimeFrameUnit.Hour   => limit + 5,
                    // For minute bars the window spans multiple trading days; page size must cover
                    // all bars in that window (daysNeeded × bars-per-day) so TakeLast(limit)
                    // returns the most recent bars rather than the oldest page.
                    BarTimeFrameUnit.Minute => (int)(MinuteWindowDays(limit, timeFrame.Value)
                                                  * (390.0 / Math.Max(1, timeFrame.Value))) + 5,
                    _                      => 1000
                };

                // For DAY and HOUR bars, if the estimated window would require more than the API
                // max page size, shrink the window so that a single page still contains the
                // *latest* bars rather than an older slice of history.
                if ((timeFrame.Unit == BarTimeFrameUnit.Day || timeFrame.Unit == BarTimeFrameUnit.Hour)
                    && rawPageSize > 10_000)
                {
                    var maxBarsUnderCap = 10_000 - 5;

                    if (timeFrame.Unit == BarTimeFrameUnit.Day)
                    {
                        // Derive the lookback window from the capped bar count using the same
                        // trading-day (5/7) conversion as the initial estimate, so that the
                        // window still spans roughly maxBarsUnderCap trading days.
                        var lookbackCalendarDays = maxBarsUnderCap * 7 / 5 + 5;
                        from = into.AddDays(-lookbackCalendarDays);
                    }
                    else // BarTimeFrameUnit.Hour
                    {
                        from = into.AddHours(-maxBarsUnderCap);
                    }

                    // Request the full page so we still get as many of the most recent bars as
                    // the API allows.
                    rawPageSize = 10_000;
                }
                // If the estimated minute-bar window would require more than the API max page size,
                // shrink the window so that a single page still contains the *latest* bars.
                if (timeFrame.Unit == BarTimeFrameUnit.Minute && rawPageSize > 10_000)
                {
                    const double tradingMinutesPerDay = 390.0; // US equity regular session
                    var barMinutes = Math.Max(1, timeFrame.Value);
                    var barsPerDay = tradingMinutesPerDay / barMinutes;

                    // Floor (not Ceiling) so effectiveDays * barsPerDay + 5 never exceeds 10 000.
                    // Ceiling(9995/390) = 26 → rawPageSize = 26×390+5 = 10 145 (too large).
                    // Floor(9995/390)   = 25 → rawPageSize = 25×390+5 = 9 755 ✓
                    var maxBarsUnderCap = 10_000 - 5;
                    var maxDaysByCap = Math.Max(1, (int)Math.Floor(maxBarsUnderCap / barsPerDay));

                    // Never expand beyond the originally requested window; only shrink when needed.
                    var originalDays = MinuteWindowDays(limit, timeFrame.Value);
                    var effectiveDays = Math.Min(originalDays, maxDaysByCap);

                    from = into.AddDays(-effectiveDays);
                    rawPageSize = (int)(effectiveDays * barsPerDay) + 5;
                }
                var request = new HistoricalBarsRequest(symbol, from, into, timeFrame)
                    .WithPageSize((uint)Math.Min(rawPageSize, 10_000));

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
                // Crypto trades 24/7. Window and page size are scaled to the timeframe unit so
                // all requested bars fit in one page and none are stale.
                // Minute bars: tight window keeps the page small and bars recent.
                // Daily/hourly bars: use the appropriate unit — AddMinutes for minutes would
                //   return an 80-minute window for a 25-bar daily request (empty result).
                //
                // Page size is clamped to the API max (10 000). The lookback window uses the
                // same clamped value as its width (in the relevant time unit) so the window
                // never spans more bars than one page can return; TakeLast(limit) therefore
                // always operates on the most recent data rather than the oldest page.
                var rawCryptoPageSize = timeFrame.Unit switch
                {
                    BarTimeFrameUnit.Day  => limit * 7 / 5 + 5,
                    BarTimeFrameUnit.Hour => limit + 5,
                    _                    => limit * 2 + 30
                };
                var pageSize = (uint)Math.Min(rawCryptoPageSize, 10_000);
                // Window width derived from the clamped page size.
                // For minute bars the window is in minutes, so multiply by the bar width
                // (timeFrame.Value) — e.g. 80 bars of 5-min each span 400 minutes, not 80.
                var from = timeFrame.Unit switch
                {
                    BarTimeFrameUnit.Day  => into.AddDays(-(int)pageSize),
                    BarTimeFrameUnit.Hour => into.AddHours(-(int)pageSize),
                    _                    => into.AddMinutes(-(int)pageSize * Math.Max(1, timeFrame.Value))
                };
                var request = new HistoricalCryptoBarsRequest(symbol, from, into, timeFrame)
                    .WithPageSize(pageSize);
                logger.LogDebug("Crypto request: {Symbol} from {From} to {Into}, pageSize {PageSize}",
                    symbol, from, into, pageSize);
                var page = await cryptoDataClient.ListHistoricalBarsAsync(request, cts.Token);
                logger.LogDebug("Crypto response: {Symbol} returned {Count} items", symbol, page.Items.Count);
                items = page.Items;
            }

            // Bars are returned ascending (oldest first); take the tail to get the most recent limit.
            var bars = items
                .TakeLast(limit)
                .Select(bar => CreateQuote(
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

        // Use the most recent 1-minute bar as a price proxy (snapshot APIs deprecated in SDK v7.2.0).
        var bars = await GetBarsAsync(symbol, "1Min", 1, ct);
        if (bars.Count == 0)
            throw new MarketDataException($"No price data available for {symbol}");

        var bar = bars[^1];
        var utcNow = DateTimeOffset.UtcNow;

        // Freshness check: reject stale bars so ExitManager never prices exits on old data.
        // ExitManager already skips equity positions when market is closed, so stale equity
        // bars will not be fetched in that context. Disabled when MaxPriceAgeSeconds = 0.
        if (_maxPriceAgeSeconds > 0)
        {
            var ageSeconds = (utcNow - bar.Timestamp).TotalSeconds;
            if (ageSeconds > _maxPriceAgeSeconds)
                throw new MarketDataException(
                    $"Stale price data for {symbol}: bar age {ageSeconds:F0}s > max {_maxPriceAgeSeconds}s");
        }

        // Close price serves as both bid and ask (no real-time spread available via bars).
        // MidPrice on BidAskSpread will equal close when bid == ask.
        return new BidAskSpread(symbol, bar.Close, bar.Close, 0m, 0m, bar.Timestamp);
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
    /// <summary>
    /// Initialises a new instance of the <see cref="MarketDataException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public MarketDataException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new instance of the <see cref="MarketDataException"/> class with a specified error message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public MarketDataException(string message, Exception innerException) : base(message, innerException) { }
}
