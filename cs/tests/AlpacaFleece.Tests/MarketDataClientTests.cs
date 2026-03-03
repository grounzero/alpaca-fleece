using Alpaca.Markets;
using AlpacaFleece.Infrastructure.Symbols;

namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for MarketDataClient (quote mapping, equity/crypto detection, API routing).
/// </summary>
public sealed class MarketDataClientTests
{
    

    private static MarketDataClient CreateClient(
        IAlpacaDataClient? equityClient = null,
        IAlpacaCryptoDataClient? cryptoClient = null,
        ISymbolClassifier? symbolClassifier = null)
    {
        equityClient ??= Substitute.For<IAlpacaDataClient>();
        cryptoClient ??= Substitute.For<IAlpacaCryptoDataClient>();
        var brokerOptions = new BrokerOptions { ApiKey = "test", SecretKey = "test", IsPaperTrading = true };
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        if (symbolClassifier == null)
        {
            var opts = new TradingOptions
            {
                Symbols = new SymbolLists
                {
                    CryptoSymbols = new List<string> { "BTCUSD", "ETHUSD", "BTCUSDT", "ETHUSDT" },
                    EquitySymbols = new List<string> { "AAPL", "MSFT", "GOOG" }
                }
            };
            symbolClassifier = new SymbolClassifier(opts.Symbols.CryptoSymbols, opts.Symbols.EquitySymbols);
        }

        return new MarketDataClient(equityClient, cryptoClient, brokerOptions, logger, symbolClassifier);
    }

    // ── Daily bar lookback regression tests ──────────────────────────────────────────────────────

    private static (MarketDataClient Client, IAlpacaCryptoDataClient MockCrypto) CreateCryptoOnlyClient()
    {
        var mockCrypto = Substitute.For<IAlpacaCryptoDataClient>();
        var emptyPage = Substitute.For<IPage<IBar>>();
        emptyPage.Items.Returns(new List<IBar>().AsReadOnly());
        mockCrypto
            .ListHistoricalBarsAsync(Arg.Any<HistoricalCryptoBarsRequest>(), Arg.Any<CancellationToken>())
            .Returns(emptyPage);

        var sc = new SymbolClassifier(new List<string> { "BTC/USD" }, new List<string>());
        var client = new MarketDataClient(
            Substitute.For<IAlpacaDataClient>(),
            mockCrypto,
            new BrokerOptions { ApiKey = "k", SecretKey = "s", IsPaperTrading = true },
            Substitute.For<ILogger<MarketDataClient>>(),
            sc);
        return (client, mockCrypto);
    }

    [Fact]
    public async Task GetBarsAsync_CryptoDailyBars_LooksBackInDays()
    {
        // Regression: before the fix, from = UtcNow - (25*2+30) minutes = 80 minutes back,
        // which returns zero daily bars. After the fix, from is limit*7/5+5 = 40 days back.
        var (client, mockCrypto) = CreateCryptoOnlyClient();

        HistoricalCryptoBarsRequest? captured = null;
        mockCrypto
            .ListHistoricalBarsAsync(
                Arg.Do<HistoricalCryptoBarsRequest>(r => captured = r),
                Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IPage<IBar>>());

        await client.GetBarsAsync("BTC/USD", "1Day", 25);

        Assert.NotNull(captured);
        // limit=25 → calendarDays = 25*7/5+5 = 40; from must be ≥ 39 days ago
        Assert.True(captured.TimeInterval.From <= DateTime.UtcNow.AddDays(-39),
            $"Expected from ≤ {DateTime.UtcNow.AddDays(-39):u}, got {captured.TimeInterval.From}");
    }

    [Fact]
    public async Task GetBarsAsync_CryptoMinuteBars_LooksBackInMinutes()
    {
        // Minute bars must keep the original tight window (limit*2+30 minutes) so that
        // TakeLast returns recent bars, not stale bars from days ago.
        var (client, mockCrypto) = CreateCryptoOnlyClient();

        HistoricalCryptoBarsRequest? captured = null;
        mockCrypto
            .ListHistoricalBarsAsync(
                Arg.Do<HistoricalCryptoBarsRequest>(r => captured = r),
                Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IPage<IBar>>());

        await client.GetBarsAsync("BTC/USD", "1m", 25);

        Assert.NotNull(captured);
        // limit=25 → window = 25*2+30 = 80 minutes; from must be within the last 2 hours
        Assert.True(captured.TimeInterval.From >= DateTime.UtcNow.AddHours(-2),
            $"Expected from ≥ {DateTime.UtcNow.AddHours(-2):u} (tight minute window), got {captured.TimeInterval.From}");
    }

    [Fact]
    public async Task GetBarsAsync_ReturnsEmptyList_WhenNoData()
    {
        var mockEquityClient = Substitute.For<IAlpacaDataClient>();
        var mockPage = Substitute.For<IPage<IBar>>();
        mockPage.Items.Returns(new List<IBar>().AsReadOnly());
        mockEquityClient
            .ListHistoricalBarsAsync(Arg.Any<HistoricalBarsRequest>(), Arg.Any<CancellationToken>())
            .Returns(mockPage);

        var client = CreateClient(equityClient: mockEquityClient);
        var result = await client.GetBarsAsync("AAPL", "1Min", 50);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBarsAsync_ThrowsException_WhenSymbolEmpty()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetBarsAsync("", "1Min", 50));
    }

    [Fact]
    public async Task GetBarsAsync_ThrowsException_WhenLimitOutOfRange()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetBarsAsync("AAPL", "1Min", 0));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetBarsAsync("AAPL", "1Min", 10001));
    }

    [Fact]
    public async Task GetBarsAsync_ThrowsException_WhenSymbolNotClassified()
    {
        // Create client with known symbol lists (AAPL, MSFT, GOOG are equities; BTCUSD, ETHUSD are crypto)
        var client = CreateClient();

        // XYZ is not in either list, should throw InvalidOperationException wrapped in MarketDataException
        var ex = await Assert.ThrowsAsync<MarketDataException>(
            async () => await client.GetBarsAsync("XYZ", "1Min", 50));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("not classified", ex.InnerException?.Message ?? "");
    }

    [Fact]
    public async Task GetBarsAsync_WrapsExceptionInMarketDataException()
    {
        var client = CreateClient();

        var ex = await Assert.ThrowsAsync<MarketDataException>(
            async () => await client.GetBarsAsync("INVALID@SYMBOL", "1Min", 50));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task GetSnapshotAsync_ThrowsNotImplementedException()
    {
        var client = CreateClient();

        // GetSnapshotAsync is not yet implemented due to Alpaca.Markets SDK snapshot API deprecation
        await Assert.ThrowsAsync<NotImplementedException>(
            async () => await client.GetSnapshotAsync("AAPL"));
    }

    [Fact]
    public async Task GetSnapshotAsync_ThrowsException_WhenSymbolEmpty()
    {
        var client = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetSnapshotAsync(""));
    }

    [Fact]
    public void IsEquity_ReturnsTrueForStockSymbols()
    {
        var client = CreateClient();

        Assert.True(client.IsEquity("AAPL"));
        Assert.True(client.IsEquity("MSFT"));
        Assert.True(client.IsEquity("GOOG"));
    }

    [Fact]
    public void IsEquity_ReturnsFalseForCryptoSymbols()
    {
        var client = CreateClient();

        Assert.False(client.IsEquity("BTCUSD"));
        Assert.False(client.IsEquity("ETHUSD"));
        Assert.False(client.IsEquity("BTCUSDT"));
        Assert.False(client.IsEquity("ETHUSDT"));
    }

    [Fact]
    public void IsCrypto_ReturnsTrueForCryptoSymbols()
    {
        var client = CreateClient();

        Assert.True(client.IsCrypto("BTCUSD"));
        Assert.True(client.IsCrypto("ETHUSD"));
    }

    [Fact]
    public void IsCrypto_ReturnsFalseForStockSymbols()
    {
        var client = CreateClient();

        Assert.False(client.IsCrypto("AAPL"));
        Assert.False(client.IsCrypto("MSFT"));
    }

    [Fact]
    public void CreateQuote_CreatesQuoteWithCorrectValues()
    {
        var client = CreateClient();
        var timestamp = DateTimeOffset.UtcNow;

        var quote = client.CreateQuote("AAPL", 150m, 152m, 148m, 151m, 1000000, timestamp);

        Assert.NotNull(quote);
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Equal(150m, quote.Open);
        Assert.Equal(152m, quote.High);
        Assert.Equal(148m, quote.Low);
        Assert.Equal(151m, quote.Close);
        Assert.Equal(1000000, quote.Volume);
    }

    [Fact]
    public void CreateQuote_LogsWarning_WhenHighLessThanLow()
    {
        var client = CreateClient();
        var timestamp = DateTimeOffset.UtcNow;

        // Should still return quote but log warning
        var quote = client.CreateQuote("AAPL", 150m, 148m, 152m, 151m, 1000000, timestamp);

        Assert.NotNull(quote);
        Assert.Equal("AAPL", quote.Symbol);
    }

    [Fact]
    public void BidAskSpread_CalculatesSpreadPercentCorrectly()
    {
        var spread = new BidAskSpread("AAPL", 150m, 150.5m, 1000, 1000, DateTimeOffset.UtcNow);

        Assert.Equal(0.333333m, spread.SpreadPercent, 5); // (150.5 - 150) / 150 * 100
    }

    [Fact]
    public void BidAskSpread_CalculatesMidPrice()
    {
        var spread = new BidAskSpread("AAPL", 150m, 151m, 1000, 1000, DateTimeOffset.UtcNow);

        Assert.Equal(150.5m, spread.MidPrice);
    }

    [Fact]
    public void BidAskSpread_HandlesZeroBidAsk()
    {
        var spread = new BidAskSpread("AAPL", 0m, 0m, 0, 0, DateTimeOffset.UtcNow);

        Assert.Equal(0m, spread.SpreadPercent);
        Assert.Equal(0m, spread.MidPrice);
    }
}
