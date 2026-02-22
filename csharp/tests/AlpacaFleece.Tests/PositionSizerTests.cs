namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for PositionSizer position sizing calculations.
/// </summary>
public sealed class PositionSizerTests
{
    [Fact]
    public void CalculateQuantity_ReturnsCorrectSize()
    {
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m,
                BarsInRegime: 15));

        var accountEquity = 100000m;
        var maxPositionPct = 0.05m; // 5%

        var qty = PositionSizer.CalculateQuantity(signal, accountEquity, maxPositionPct);

        // qty = (100000 * 0.05) / 150 = 5000 / 150 = 33.33... → 33
        var expectedQty = Math.Floor((accountEquity * maxPositionPct) / signal.Metadata.CurrentPrice);
        Assert.Equal(expectedQty, qty);
    }

    [Fact]
    public void CalculateQuantity_EnforcesMinimumOfOneShare()
    {
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 5000m,
                MediumSma: 4999m,
                SlowSma: 4995m,
                Atr: 50m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 5000m,
                BarsInRegime: 15));

        var accountEquity = 1000m; // Very small account
        var maxPositionPct = 0.01m;

        var qty = PositionSizer.CalculateQuantity(signal, accountEquity, maxPositionPct);

        // qty = (1000 * 0.01) / 5000 = 10 / 5000 = 0.002 → max(1) = 1
        Assert.Equal(1m, qty);
    }

    [Fact]
    public void CalculateQuantity_RespectsDifferentMaxPositionPercentages()
    {
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 100m,
                BarsInRegime: 15));

        var accountEquity = 100000m;

        var qty05Pct = PositionSizer.CalculateQuantity(signal, accountEquity, 0.05m);
        var qty10Pct = PositionSizer.CalculateQuantity(signal, accountEquity, 0.10m);

        // Larger max position percent should result in larger quantity
        Assert.True(qty10Pct > qty05Pct);
        Assert.Equal(qty05Pct * 2, qty10Pct);
    }

    [Fact]
    public void CalculateQuantity_ThrowsOnNullSignal()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => PositionSizer.CalculateQuantity(null!, 100000m, 0.05m));

        Assert.Equal("signal", ex.ParamName);
    }

    [Fact]
    public void CalculateQuantity_ThrowsOnInvalidEquity()
    {
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m,
                BarsInRegime: 15));

        var ex = Assert.Throws<ArgumentException>(
            () => PositionSizer.CalculateQuantity(signal, 0m, 0.05m));

        Assert.Contains("Account equity", ex.Message);
    }

    [Fact]
    public void CalculateQuantity_ThrowsOnInvalidMaxPositionPercent()
    {
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m,
                BarsInRegime: 15));

        var ex = Assert.Throws<ArgumentException>(
            () => PositionSizer.CalculateQuantity(signal, 100000m, 1.5m));

        Assert.Contains("Max position percent", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Dual-formula overload tests
    // -----------------------------------------------------------------------

    [Fact]
    public void CalculateQuantity_RiskBasedCap_WhenTighterThanEquityCap()
    {
        // equity=100000, price=100, maxPosPct=0.10 → equity qty = 100
        // riskPct=0.01, stopPct=0.50 → risk qty = (100000*0.01)/(100*0.50) = 1000/50 = 20
        // result = min(100, 20) = 20
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 2m, Confidence: 0.8m, Regime: "TRENDING_UP",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        var qty = PositionSizer.CalculateQuantity(signal, 100000m, 0.10m, 0.01m, 0.50m);

        Assert.Equal(20m, qty);
    }

    [Fact]
    public void CalculateQuantity_EquityCap_WhenTighterThanRiskCap()
    {
        // equity=100000, price=100, maxPosPct=0.01 → equity qty = 10
        // riskPct=0.05, stopPct=0.01 → risk qty = (100000*0.05)/(100*0.01) = 5000/1 = 5000
        // result = min(10, 5000) = 10
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 2m, Confidence: 0.8m, Regime: "TRENDING_UP",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        var qty = PositionSizer.CalculateQuantity(signal, 100000m, 0.01m, 0.05m, 0.01m);

        Assert.Equal(10m, qty);
    }

    [Fact]
    public void CalculateQuantity_DualFormula_EnforcesMinimumOfOne()
    {
        // Very tight risk cap that would yield < 1
        // equity=100, price=100, riskPct=0.001, stopPct=0.50 → risk qty = 0.2 → capped at 1
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 2m, Confidence: 0.8m, Regime: "TRENDING_UP",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        var qty = PositionSizer.CalculateQuantity(signal, 100m, 0.10m, 0.001m, 0.50m);

        Assert.Equal(1m, qty);
    }

    [Fact]
    public void IsValidQuantity_ReturnsTrueForValidQuantity()
    {
        var isValid = PositionSizer.IsValidQuantity(10m, 100m);
        Assert.True(isValid);
    }

    [Fact]
    public void IsValidQuantity_ReturnsFalseForZeroQuantity()
    {
        var isValid = PositionSizer.IsValidQuantity(0m, 100m);
        Assert.False(isValid);
    }

    [Fact]
    public void IsValidQuantity_ReturnsFalseForExcessiveQuantity()
    {
        var isValid = PositionSizer.IsValidQuantity(150m, 100m);
        Assert.False(isValid);
    }
}
