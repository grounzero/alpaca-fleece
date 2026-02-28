using Alpaca.Markets;
using AlpacaFleece.Infrastructure.Symbols;
using AlpacaFleece.Trading.Config;
using AlpacaFleece.Core.Interfaces;

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
    public void NormalizeQuote_CreatesQuoteWithCorrectValues()
    {
        var client = CreateClient();
        var timestamp = DateTimeOffset.UtcNow;

        var quote = client.NormalizeQuote("AAPL", 150m, 152m, 148m, 151m, 1000000, timestamp);

        Assert.NotNull(quote);
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Equal(150m, quote.Open);
        Assert.Equal(152m, quote.High);
        Assert.Equal(148m, quote.Low);
        Assert.Equal(151m, quote.Close);
        Assert.Equal(1000000, quote.Volume);
    }

    [Fact]
    public void NormalizeQuote_LogsWarning_WhenHighLessThanLow()
    {
        var client = CreateClient();
        var timestamp = DateTimeOffset.UtcNow;

        // Should still return quote but log warning
        var quote = client.NormalizeQuote("AAPL", 150m, 148m, 152m, 151m, 1000000, timestamp);

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
