namespace AlpacaFleece.Trading.Config;

/// <summary>
/// Root trading configuration loaded from appsettings.json.
/// </summary>
public sealed class TradingOptions
{
    public SymbolsOptions Symbols { get; set; } = new();
    public SessionOptions Session { get; set; } = new();
    public RiskLimits RiskLimits { get; set; } = new();
    public ExitOptions Exit { get; set; } = new();
    public ExecutionOptions Execution { get; set; } = new();
    public FiltersOptions Filters { get; set; } = new();
}

/// <summary>
/// Symbols configuration.
/// </summary>
public sealed class SymbolsOptions
{
    public List<string> Symbols { get; set; } = new();
    public int MinVolume { get; set; } = 1000000;
}

/// <summary>
/// Session configuration.
/// </summary>
public sealed class SessionOptions
{
    public string TimeZone { get; set; } = "America/New_York";
    public TimeSpan MarketOpenTime { get; set; } = TimeSpan.Parse("09:30");
    public TimeSpan MarketCloseTime { get; set; } = TimeSpan.Parse("16:00");
}

/// <summary>
/// Risk limits configuration.
/// </summary>
public sealed class RiskLimits
{
    public decimal MaxDailyLoss { get; set; } = 500m;
    public decimal MaxTradeRisk { get; set; } = 100m;
    public int MaxTradesPerDay { get; set; } = 5;
    public int MaxConcurrentPositions { get; set; } = 2;
}

/// <summary>
/// Exit rules configuration.
/// </summary>
public sealed class ExitOptions
{
    // Check interval in seconds (default 30s)
    public int CheckIntervalSeconds { get; set; } = 30;

    // Exponential backoff base in seconds for failed exit attempts
    public int BackoffBaseSeconds { get; set; } = 2;

    // Maximum backoff cap in seconds
    public int BackoffMaxSeconds { get; set; } = 300;

    // ATR multiplier for stop loss level (default 1.5)
    public decimal AtrStopLossMultiplier { get; set; } = 1.5m;

    // ATR multiplier for profit target level (default 3.0)
    public decimal AtrProfitTargetMultiplier { get; set; } = 3.0m;

    // Fixed percentage stop loss (default 1%)
    public decimal StopLossPercentage { get; set; } = 0.01m;

    // Fixed percentage profit target (default 2%)
    public decimal ProfitTargetPercentage { get; set; } = 0.02m;

    // Trailing stop percent (e.g., 2 = 2%)
    public decimal TrailingStopPercent { get; set; } = 2m;
}

/// <summary>
/// Execution configuration.
/// </summary>
public sealed class ExecutionOptions
{
    public bool DryRun { get; set; } = false;
    public bool KillSwitch { get; set; } = false;
}

/// <summary>
/// Filters configuration (spread, volume, time-of-day).
/// </summary>
public sealed class FiltersOptions
{
    public decimal MaxBidAskSpreadPercent { get; set; } = 0.1m;
    public long MinBarVolume { get; set; } = 100000;
    public int MinMinutesAfterOpen { get; set; } = 5;
    public int MinMinutesBeforeClose { get; set; } = 10;
}
