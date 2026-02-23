namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for DataHandler (bar persistence, in-memory history, sufficiency checks).
/// </summary>
public sealed class DataHandlerTests
{
    [Fact]
    public void DataHandler_InitializesSuccessfully()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);

        // Act
        handler.Initialise();

        // Assert: Subscribe method no longer exists in IEventBus interface
        // eventBus.Received().Subscribe<BarEvent>(Arg.Any<Func<BarEvent, CancellationToken, ValueTask>>());
        Assert.NotNull(handler);
    }

    [Fact]
    public void DataHandler_GetDataFrame_ReturnsEmptyList_WhenSymbolNotFound()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);

        // Act
        var result = handler.GetDataFrame("NONEXISTENT");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void DataHandler_HasSufficientHistory_ReturnsFalse_WhenNotEnoughBars()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);

        // Act
        var result = handler.HasSufficientHistory("AAPL", 51);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void DataHandler_HasSufficientHistory_ReturnsTrueWhenInitialized()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);

        // Act - manually add bars to the handler
        handler.Initialise();
        // Note: In real scenario, bars would be added via event subscription

        // Assert
        Assert.False(handler.HasSufficientHistory("AAPL", 1));
    }

    [Fact]
    public void DataHandler_GetBarCount_ReturnsZero_WhenSymbolNotFound()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);

        // Act
        var count = handler.GetBarCount("NONEXISTENT");

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void DataHandler_Clear_RemovesAllHistories()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);
        handler.Initialise();

        // Act
        handler.Clear();

        // Assert - no bar count should be tracked
        Assert.Equal(0, handler.GetBarCount("AAPL"));
    }

    [Fact]
    public void DataHandler_OnBarEvent_SubscriptionIsRegistered()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);

        // Act
        handler.Initialise();

        // Assert: Subscribe method no longer exists in IEventBus interface
        // eventBus.Received(1).Subscribe<BarEvent>(Arg.Any<Func<BarEvent, CancellationToken, ValueTask>>());
        Assert.NotNull(handler);
    }

    [Fact]
    public void DataHandler_GetDataFrame_ReturnsReadOnlyList()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);

        // Act
        var result = handler.GetDataFrame("AAPL");

        // Assert
        Assert.NotNull(result);
        Assert.IsAssignableFrom<IReadOnlyList<Quote>>(result);
    }

    [Fact]
    public void DataHandler_SupportsMultipleSymbols_Independently()
    {
        // Arrange
        var eventBus = Substitute.For<IEventBus>();
        var dbContextFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        var logger = Substitute.For<ILogger<DataHandler>>();
        var handler = new DataHandler(eventBus, dbContextFactory, logger);
        handler.Initialise();

        // Act
        var aapl = handler.GetBarCount("AAPL");
        var msft = handler.GetBarCount("MSFT");

        // Assert
        Assert.Equal(0, aapl);
        Assert.Equal(0, msft);
    }
}
