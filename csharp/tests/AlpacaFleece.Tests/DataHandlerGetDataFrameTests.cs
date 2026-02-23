namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for DataHandler.GetDataFrame() implementation.
/// </summary>
public sealed class DataHandlerGetDataFrameTests
{
    private readonly IEventBus _eventBus;
    private readonly IDbContextFactory<TradingDbContext> _dbContextFactory;
    private readonly ILogger<DataHandler> _logger;
    private readonly DataHandler _dataHandler;

    public DataHandlerGetDataFrameTests()
    {
        _eventBus = Substitute.For<IEventBus>();
        _dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        _logger = Substitute.For<ILogger<DataHandler>>();
        _dataHandler = new DataHandler(_eventBus, _dbContextFactory, _logger);
    }

    [Fact]
    public void GetDataFrame_UnknownSymbol_ReturnsEmptyList()
    {
        var result = _dataHandler.GetDataFrame("UNKNOWN");

        Assert.Empty(result);
    }

    [Fact]
    public void GetDataFrame_WithHistory_ReturnsQuotes()
    {
        var bar = new BarEvent(
            Symbol: "AAPL",
            Timeframe: "1m",
            Timestamp: DateTimeOffset.UtcNow,
            Open: 100m,
            High: 102m,
            Low: 99m,
            Close: 101m,
            Volume: 1000);

        _dataHandler.OnBarAsync(bar, CancellationToken.None).GetAwaiter().GetResult();

        var result = _dataHandler.GetDataFrame("AAPL");

        Assert.NotEmpty(result);
        Assert.Single(result);
        Assert.Equal("AAPL", result[0].Symbol);
        Assert.Equal(100m, result[0].Open);
        Assert.Equal(102m, result[0].High);
        Assert.Equal(99m, result[0].Low);
        Assert.Equal(101m, result[0].Close);
        Assert.Equal(1000, result[0].Volume);
    }

    [Fact]
    public void GetDataFrame_MultipleSymbols_ReturnsCorrectSymbol()
    {
        var bar1 = new BarEvent("AAPL", "1m", DateTimeOffset.UtcNow, 100m, 102m, 99m, 101m, 1000);
        var bar2 = new BarEvent("MSFT", "1m", DateTimeOffset.UtcNow, 200m, 202m, 199m, 201m, 2000);

        _dataHandler.OnBarAsync(bar1, CancellationToken.None).GetAwaiter().GetResult();
        _dataHandler.OnBarAsync(bar2, CancellationToken.None).GetAwaiter().GetResult();

        var aapl = _dataHandler.GetDataFrame("AAPL");
        var msft = _dataHandler.GetDataFrame("MSFT");

        Assert.Single(aapl);
        Assert.Single(msft);
        Assert.Equal("AAPL", aapl[0].Symbol);
        Assert.Equal("MSFT", msft[0].Symbol);
        Assert.Equal(101m, aapl[0].Close); // bar1 Close is 101m
        Assert.Equal(201m, msft[0].Close);
    }

    [Fact]
    public void GetDataFrame_MultipleBars_PreservesOrder()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: now.AddMinutes(i),
                Open: 100m + i,
                High: 102m + i,
                Low: 99m + i,
                Close: 101m + i,
                Volume: 1000 + i);

            _dataHandler.OnBarAsync(bar, CancellationToken.None).GetAwaiter().GetResult();
        }

        var result = _dataHandler.GetDataFrame("AAPL");

        Assert.Equal(5, result.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(100m + i, result[i].Open);
            Assert.Equal(101m + i, result[i].Close);
        }
    }
}
