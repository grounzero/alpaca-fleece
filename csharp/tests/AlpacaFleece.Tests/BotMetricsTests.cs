namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for BotMetrics (thread-safe counters, JSON persistence).
/// </summary>
public sealed class BotMetricsTests
{
    private readonly ILogger<BotMetrics> _logger = Substitute.For<ILogger<BotMetrics>>();

    [Fact]
    public void Counters_IncrementCorrectly()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);

        // Act
        metrics.IncrementSignalsGenerated();
        metrics.IncrementSignalsGenerated();
        metrics.IncrementOrdersSubmitted();
        metrics.IncrementOrdersFilled();
        metrics.IncrementExitsTriggered();

        // Assert
        Assert.Equal(2, metrics.SignalsGenerated);
        Assert.Equal(1, metrics.OrdersSubmitted);
        Assert.Equal(1, metrics.OrdersFilled);
        Assert.Equal(1, metrics.ExitsTriggered);
    }

    [Fact]
    public void Gauges_SetCorrectly()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);

        // Act
        metrics.IncrementOpenPositions();
        metrics.IncrementOpenPositions();
        metrics.DailyPnl = 1234.56m;
        metrics.SetDailyTradeCount(5);
        metrics.EquityValue = 100000m;

        // Assert
        Assert.Equal(2, metrics.OpenPositions);
        Assert.Equal(1234.56m, metrics.DailyPnl);
        Assert.Equal(5, metrics.DailyTradeCount);
        Assert.Equal(100000m, metrics.EquityValue);
    }

    [Fact]
    public void DecrementOpenPositions_DecreasesCount()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);
        metrics.IncrementOpenPositions();
        metrics.IncrementOpenPositions();

        // Act
        metrics.DecrementOpenPositions();

        // Assert
        Assert.Equal(1, metrics.OpenPositions);
    }

    [Fact]
    public async Task WriteToFileAsync_CreatesMetricsJson()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);
        metrics.IncrementSignalsGenerated();
        metrics.IncrementOrdersSubmitted();
        metrics.DailyPnl = 500m;
        metrics.EquityValue = 105000m;

        var tmpFile = Path.GetTempFileName();
        try
        {
            // Act
            await metrics.WriteToFileAsync(tmpFile, CancellationToken.None);

            // Assert
            Assert.True(File.Exists(tmpFile));
            var content = await File.ReadAllTextAsync(tmpFile);
            Assert.Contains("SignalsGenerated", content);
            Assert.Contains("OrdersSubmitted", content);
            Assert.Contains("DailyPnl", content);
            Assert.Contains("EquityValue", content);
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [Fact]
    public async Task WriteToFileAsync_DefaultsToDataDirectory()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);
        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        var metricsPath = Path.Combine(dataDir, "metrics.json");

        try
        {
            // Clean up any existing file
            if (File.Exists(metricsPath))
            {
                File.Delete(metricsPath);
            }

            // Act
            await metrics.WriteToFileAsync(ct: CancellationToken.None);

            // Assert
            await Task.Delay(100); // Give async write time
            Assert.True(File.Exists(metricsPath));
        }
        finally
        {
            if (File.Exists(metricsPath))
            {
                File.Delete(metricsPath);
            }
        }
    }

    [Fact]
    public void GetSummary_ReturnsFormattedString()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);
        metrics.IncrementSignalsGenerated();
        metrics.IncrementSignalsFiltered();
        metrics.SetDailyTradeCount(3);
        metrics.DailyPnl = 250m;

        // Act
        var summary = metrics.GetSummary();

        // Assert
        Assert.Contains("Signals Generated", summary);
        Assert.Contains("Signals Filtered", summary);
        Assert.Contains("Daily Trades", summary);
        Assert.Contains("Daily P&L", summary);
    }

    [Fact]
    public void Counters_AreThreadSafe()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    metrics.IncrementSignalsGenerated();
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        Assert.Equal(1000, metrics.SignalsGenerated);
    }

    [Fact]
    public void EventsDropped_Increments()
    {
        // Arrange
        var metrics = new BotMetrics(_logger);

        // Act
        metrics.IncrementEventsDropped();
        metrics.IncrementEventsDropped();
        metrics.IncrementEventsDropped();

        // Assert
        Assert.Equal(3, metrics.EventsDropped);
    }
}
