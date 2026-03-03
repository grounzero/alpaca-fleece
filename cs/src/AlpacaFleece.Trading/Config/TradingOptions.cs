namespace AlpacaFleece.Trading.Config;

/// <summary>
/// Root trading configuration loaded from appsettings.json.
/// </summary>
public sealed class TradingOptions
{
    public SymbolLists Symbols { get; set; } = new();
    public SessionOptions Session { get; set; } = new();
    public RiskLimits RiskLimits { get; set; } = new();
    public ExitOptions Exit { get; set; } = new();
    public ExecutionOptions Execution { get; set; } = new();
    public FiltersOptions Filters { get; set; } = new();
    public DrawdownOptions Drawdown { get; set; } = new();
    public CorrelationLimitsOptions CorrelationLimits { get; set; } = new();
}

/// <summary>
/// Explicit symbol classification lists.
/// </summary>
public sealed class SymbolLists
{
    /// <summary>
    /// Crypto symbols that trade 24/5 (exempt from market-hours checks).
    /// </summary>
    public List<string> CryptoSymbols { get; set; } = new();

    /// <summary>
    /// Equity symbols (regular US equities) used for market-data and strategies.
    /// </summary>
    public List<string> EquitySymbols { get; set; } = new();

    /// <summary>
    /// Minimum bar volume used by filters (kept for compatibility of semantics).
    /// </summary>
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

    /// <summary>
    /// Maximum fraction of equity risked per trade (e.g., 0.01 = 1%).
    /// Used in the risk-based position sizing formula.
    /// </summary>
    public decimal MaxRiskPerTradePct { get; set; } = 0.01m;

    /// <summary>
    /// Expected stop-loss as a fraction of price (e.g., 0.02 = 2%).
    /// Used in the risk-based position sizing formula: qty = equity * riskPct / (price * stopPct).
    /// </summary>
    public decimal StopLossPct { get; set; } = 0.02m;

    /// <summary>
    /// Minimum signal confidence threshold (0-1). Signals below this are soft-skipped (FILTER tier).
    /// </summary>
    public decimal MinSignalConfidence { get; set; } = 0.5m;
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

    /// <summary>
    /// Maximum allowed bid/ask spread as a fraction of bid price (e.g. 0.005 = 0.5%).
    /// Signals with a wider spread are soft-skipped (FILTER tier).
    /// </summary>
    public decimal MaxSpreadPct { get; set; } = 0.005m;
}

/// <summary>
/// Drawdown monitoring configuration.
/// </summary>
public sealed class DrawdownOptions
{
    /// <summary>
    /// Enable or disable drawdown monitoring (default: true).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Rolling lookback period in days for peak equity reset (default: 20).
    /// Peak is reset to current equity at the start of each lookback window.
    /// </summary>
    public int LookbackDays { get; set; } = 20;

    /// <summary>
    /// Enable automatic recovery from drawdown states (default: true).
    /// When true, levels descend (Halt → Normal) when drawdown falls below recovery thresholds.
    /// When false, manual intervention required (system restart with recovery flag).
    /// </summary>
    public bool EnableAutoRecovery { get; set; } = true;

    /// <summary>
    /// Drawdown % at which Warning is triggered — position sizes reduced (default: 3%).
    /// </summary>
    public decimal WarningThresholdPct { get; set; } = 0.03m;

    /// <summary>
    /// Drawdown % at which Recovery from Warning state occurs (default: 2%).
    /// Only used if EnableAutoRecovery is true.
    /// </summary>
    public decimal WarningRecoveryThresholdPct { get; set; } = 0.02m;

    /// <summary>
    /// Drawdown % at which Halt is triggered — no new positions (default: 5%).
    /// </summary>
    public decimal HaltThresholdPct { get; set; } = 0.05m;

    /// <summary>
    /// Drawdown % at which Recovery from Halt state occurs (default: 4%).
    /// Only used if EnableAutoRecovery is true.
    /// </summary>
    public decimal HaltRecoveryThresholdPct { get; set; } = 0.04m;

    /// <summary>
    /// Drawdown % at which Emergency is triggered — all positions closed (default: 10%).
    /// </summary>
    public decimal EmergencyThresholdPct { get; set; } = 0.10m;

    /// <summary>
    /// Drawdown % at which Recovery from Emergency state occurs (default: 8%).
    /// Only used if EnableAutoRecovery is true.
    /// </summary>
    public decimal EmergencyRecoveryThresholdPct { get; set; } = 0.08m;

    /// <summary>
    /// Position size multiplier during Warning state (default: 0.5 = 50%).
    /// </summary>
    public decimal WarningPositionMultiplier { get; set; } = 0.5m;

    /// <summary>
    /// Drawdown check interval in seconds (default: 60).
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Correlation and concentration limit configuration.
/// </summary>
public sealed class CorrelationLimitsOptions
{
    /// <summary>
    /// Enable or disable all correlation and concentration checks (default: true).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum allowed pairwise correlation coefficient before a new signal is rejected (default: 0.70).
    /// Pairs with a higher correlation in StaticCorrelations will be blocked.
    /// </summary>
    public decimal MaxCorrelation { get; set; } = 0.70m;

    /// <summary>
    /// Maximum fraction of total position capacity allowed in any single GICS sector (default: 0.20 = 20%).
    /// Calculated as (positionsInSector + 1) / MaxConcurrentPositions.
    /// </summary>
    public decimal MaxSectorPct { get; set; } = 0.20m;

    /// <summary>
    /// Maximum fraction of total position capacity allowed in any single asset class (default: 0.40 = 40%).
    /// Calculated as (positionsInClass + 1) / MaxConcurrentPositions.
    /// </summary>
    public decimal MaxAssetClassPct { get; set; } = 0.40m;

    /// <summary>
    /// Static pairwise correlation values loaded from configuration.
    /// Keys are "SYMBOL_A:SYMBOL_B" — both orderings are tried on lookup.
    /// </summary>
    public Dictionary<string, decimal> StaticCorrelations { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
