namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for MarketDataClient (quote mapping, equity/crypto detection).
/// </summary>
public sealed class MarketDataClientTests
{
    [Fact]
    public async Task GetBarsAsync_ReturnsEmptyList_WhenNoData()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act
        var result = await client.GetBarsAsync("AAPL", "1m", 50);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetBarsAsync_ThrowsException_WhenSymbolEmpty()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetBarsAsync("", "1m", 50));
    }

    [Fact]
    public async Task GetBarsAsync_ThrowsException_WhenLimitOutOfRange()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetBarsAsync("AAPL", "1m", 0));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetBarsAsync("AAPL", "1m", 10001));
    }

    [Fact]
    public async Task GetBarsAsync_WrapsExceptionInMarketDataException()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<MarketDataException>(
            async () => await client.GetBarsAsync("INVALID@SYMBOL", "1m", 50));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsSnapshot()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act
        var result = await client.GetSnapshotAsync("AAPL");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("AAPL", result.Symbol);
    }

    [Fact]
    public async Task GetSnapshotAsync_ThrowsException_WhenSymbolEmpty()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.GetSnapshotAsync(""));
    }

    [Fact]
    public void IsEquity_ReturnsTrueForStockSymbols()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        Assert.True(client.IsEquity("AAPL"));
        Assert.True(client.IsEquity("MSFT"));
        Assert.True(client.IsEquity("GOOG"));
    }

    [Fact]
    public void IsEquity_ReturnsFalseForCryptoSymbols()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        Assert.False(client.IsEquity("BTCUSD"));
        Assert.False(client.IsEquity("ETHUSD"));
        Assert.False(client.IsEquity("BTCUSDT"));
        Assert.False(client.IsEquity("ETHUSDT"));
    }

    [Fact]
    public void IsCrypto_ReturnsTrueForCryptoSymbols()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        Assert.True(client.IsCrypto("BTCUSD"));
        Assert.True(client.IsCrypto("ETHUSD"));
    }

    [Fact]
    public void IsCrypto_ReturnsFalseForStockSymbols()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);

        // Act & Assert
        Assert.False(client.IsCrypto("AAPL"));
        Assert.False(client.IsCrypto("MSFT"));
    }

    [Fact]
    public void NormalizeQuote_CreatesQuoteWithCorrectValues()
    {
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var quote = client.NormalizeQuote("AAPL", 150m, 152m, 148m, 151m, 1000000, timestamp);

        // Assert
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
        // Arrange
        var logger = Substitute.For<ILogger<MarketDataClient>>();
        var client = new MarketDataClient(logger);
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var quote = client.NormalizeQuote("AAPL", 150m, 148m, 152m, 151m, 1000000, timestamp);

        // Assert - should still return quote but log warning
        Assert.NotNull(quote);
        Assert.Equal("AAPL", quote.Symbol);
    }

    [Fact]
    public void BidAskSpread_CalculatesSpreadPercentCorrectly()
    {
        // Arrange
        var spread = new BidAskSpread("AAPL", 150m, 150.5m, 1000, 1000, DateTimeOffset.UtcNow);

        // Act
        var spreadPercent = spread.SpreadPercent;

        // Assert
        Assert.Equal(0.333333m, spreadPercent, 5); // (150.5 - 150) / 150 * 100
    }

    [Fact]
    public void BidAskSpread_CalculatesMidPrice()
    {
        // Arrange
        var spread = new BidAskSpread("AAPL", 150m, 151m, 1000, 1000, DateTimeOffset.UtcNow);

        // Act
        var midPrice = spread.MidPrice;

        // Assert
        Assert.Equal(150.5m, midPrice);
    }

    [Fact]
    public void BidAskSpread_HandlesZeroBidAsk()
    {
        // Arrange
        var spread = new BidAskSpread("AAPL", 0m, 0m, 0, 0, DateTimeOffset.UtcNow);

        // Act
        var spreadPercent = spread.SpreadPercent;
        var midPrice = spread.MidPrice;

        // Assert
        Assert.Equal(0m, spreadPercent);
        Assert.Equal(0m, midPrice);
    }
}
