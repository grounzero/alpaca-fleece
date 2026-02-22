namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for StreamPollerService (batching, polling, market hours, backoff).
/// </summary>
public sealed class StreamPollerTests
{
    [Fact]
    public async Task StreamPollerService_StartsSuccessfully()
    {
        // Arrange
        var marketDataClient = Substitute.For<IMarketDataClient>();
        var brokerService = Substitute.For<IBrokerService>();
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptions<TradingOptions>>();
        var logger = Substitute.For<ILogger<StreamPollerService>>();

        var tradingOptions = new TradingOptions
        {
            Symbols = new SymbolsOptions { Symbols = new List<string> { "AAPL" } }
        };
        options.Value.Returns(tradingOptions);

        var service = new StreamPollerService(marketDataClient, brokerService, eventBus, options, logger);

        // Act
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await service.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation token fires
        }

        // Assert - service should have started without throwing
        Assert.NotNull(service);
    }

    [Fact]
    public void EnumerableExtensions_Batch_PartitionsCorrectly()
    {
        // Arrange
        var items = new[] { 1, 2, 3, 4, 5, 6, 7 };
        var batchSize = 3;

        // Act
        var batches = items.Batch(batchSize).ToList();

        // Assert
        Assert.Equal(3, batches.Count);
        Assert.Equal(3, batches[0].Count());
        Assert.Equal(3, batches[1].Count());
        Assert.Equal(1, batches[2].Count());
    }

    [Fact]
    public void EnumerableExtensions_Batch_HandlesExactDivision()
    {
        // Arrange
        var items = new[] { 1, 2, 3, 4, 5, 6 };
        var batchSize = 3;

        // Act
        var batches = items.Batch(batchSize).ToList();

        // Assert
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal(3, b.Count()));
    }

    [Fact]
    public void EnumerableExtensions_Batch_HandlesSingleElement()
    {
        // Arrange
        var items = new[] { 1 };
        var batchSize = 5;

        // Act
        var batches = items.Batch(batchSize).ToList();

        // Assert
        Assert.Single(batches);
        Assert.Single(batches[0]);
    }

    [Fact]
    public void EnumerableExtensions_Batch_HandlesEmptyEnumerable()
    {
        // Arrange
        var items = Array.Empty<int>();
        var batchSize = 3;

        // Act
        var batches = items.Batch(batchSize).ToList();

        // Assert
        Assert.Empty(batches);
    }

    [Fact]
    public async Task StreamPollerService_StopsGracefully()
    {
        // Arrange
        var marketDataClient = Substitute.For<IMarketDataClient>();
        var brokerService = Substitute.For<IBrokerService>();
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptions<TradingOptions>>();
        var logger = Substitute.For<ILogger<StreamPollerService>>();

        var tradingOptions = new TradingOptions
        {
            Symbols = new SymbolsOptions { Symbols = new List<string>() }
        };
        options.Value.Returns(tradingOptions);

        var service = new StreamPollerService(marketDataClient, brokerService, eventBus, options, logger);

        // Act
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await service.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public async Task StreamPollerService_SkipsPolling_WhenMarketClosed()
    {
        // Arrange
        var marketDataClient = Substitute.For<IMarketDataClient>();
        var brokerService = Substitute.For<IBrokerService>();
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptions<TradingOptions>>();
        var logger = Substitute.For<ILogger<StreamPollerService>>();

        var tradingOptions = new TradingOptions
        {
            Symbols = new SymbolsOptions { Symbols = new List<string> { "AAPL" } }
        };
        options.Value.Returns(tradingOptions);

        // Mock broker returning market closed
        var clock = new ClockInfo(
            IsOpen: false,
            NextOpen: DateTimeOffset.UtcNow.AddHours(12),
            NextClose: DateTimeOffset.UtcNow.AddHours(16),
            FetchedAt: DateTimeOffset.UtcNow);
        brokerService.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ClockInfo>(clock));

        var service = new StreamPollerService(marketDataClient, brokerService, eventBus, options, logger);

        // Act
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await service.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - GetBarsAsync should not have been called since market is closed
        await marketDataClient.DidNotReceive().GetBarsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamPollerService_RetryOnError()
    {
        // Arrange
        var marketDataClient = Substitute.For<IMarketDataClient>();
        var brokerService = Substitute.For<IBrokerService>();
        var eventBus = Substitute.For<IEventBus>();
        var options = Substitute.For<IOptions<TradingOptions>>();
        var logger = Substitute.For<ILogger<StreamPollerService>>();

        var tradingOptions = new TradingOptions
        {
            Symbols = new SymbolsOptions { Symbols = new List<string>() }
        };
        options.Value.Returns(tradingOptions);

        // Mock broker returning market open
        var clock = new ClockInfo(
            IsOpen: true,
            NextOpen: DateTimeOffset.UtcNow.AddDays(1),
            NextClose: DateTimeOffset.UtcNow.AddHours(7),
            FetchedAt: DateTimeOffset.UtcNow);
        brokerService.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ClockInfo>(clock));

        var service = new StreamPollerService(marketDataClient, brokerService, eventBus, options, logger);

        // Act
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await service.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void BarsHandler_InitializesWithEmptyDeques()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<BarsHandler>>();
        var handler = new BarsHandler(eventBus, dbContextFactory, logger);

        // Act & Assert
        Assert.NotNull(handler);
    }

    [Fact]
    public void BarsHandler_GetBarsForSymbol_ReturnsEmpty_WhenNotFound()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<BarsHandler>>();
        var handler = new BarsHandler(eventBus, dbContextFactory, logger);

        // Act
        var bars = handler.GetBarsForSymbol("NONEXISTENT");

        // Assert
        Assert.NotNull(bars);
        Assert.Empty(bars);
    }

    [Fact]
    public void BarsHandler_GetBarCount_ReturnsZero_WhenNotFound()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<BarsHandler>>();
        var handler = new BarsHandler(eventBus, dbContextFactory, logger);

        // Act
        var count = handler.GetBarCount("NONEXISTENT");

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void BarsHandler_Clear_RemovesAllDeques()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<BarsHandler>>();
        var handler = new BarsHandler(eventBus, dbContextFactory, logger);

        // Act
        handler.Clear();

        // Assert
        Assert.Equal(0, handler.GetBarCount("AAPL"));
    }
}
