namespace AlpacaFleece.Trading.Config;

/// <summary>
/// Root trading configuration loaded from appsettings.json.
/// </summary>
/// <example>
/// <code>
/// var options = new TradingOptions
/// {
///     Symbols = new SymbolLists { EquitySymbols = new[] { "AAPL", "MSFT" }.ToList() },
///     RiskLimits = new RiskLimits { MaxDailyLoss = 500m },
///     Session = new SessionOptions { TimeZone = "America/New_York" }
/// };
/// </code>
/// </example>
public sealed class TradingOptions
{
    /// <summary>
    /// Gets or sets the symbol classification lists (crypto and equity).
    /// </summary>
    public SymbolLists Symbols { get; set; } = new();

    /// <summary>
    /// Gets or sets the session configuration (market hours, timezone).
    /// </summary>
    public SessionOptions Session { get; set; } = new();

    /// <summary>
    /// Gets or sets the risk limits configuration.
    /// </summary>
    public RiskLimits RiskLimits { get; set; } = new();

    /// <summary>
    /// Gets or sets the exit rules configuration.
    /// </summary>
    public ExitOptions Exit { get; set; } = new();

    /// <summary>
    /// Gets or sets the execution configuration.
    /// </summary>
    public ExecutionOptions Execution { get; set; } = new();

    /// <summary>
    /// Gets or sets the filters configuration (spread, volume, time-of-day).
    /// </summary>
    public FiltersOptions Filters { get; set; } = new();

    /// <summary>
    /// Gets or sets the drawdown monitoring configuration.
    /// </summary>
    public DrawdownOptions Drawdown { get; set; } = new();

    /// <summary>
    /// Gets or sets the correlation limits options.
    /// </summary>
    public CorrelationLimitsOptions CorrelationLimits { get; set; } = new();

    /// <summary>
    /// Gets or sets the signal filter options.
    /// </summary>
    public SignalFilterOptions SignalFilters { get; set; } = new();

    /// <summary>
    /// Gets or sets volatility-regime options used for adaptive sizing and exit distances.
    /// </summary>
    public VolatilityRegimeOptions VolatilityRegime { get; set; } = new();
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
    /// <summary>
    /// Gets or sets the timezone for market hours (default: America/New_York).
    /// </summary>
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>
    /// Gets or sets the market opening time in hours:minutes format (default: 09:30).
    /// </summary>
    public TimeSpan MarketOpenTime { get; set; } = TimeSpan.Parse("09:30");

    /// <summary>
    /// Gets or sets the market closing time in hours:minutes format (default: 16:00).
    /// </summary>
    public TimeSpan MarketCloseTime { get; set; } = TimeSpan.Parse("16:00");
}

/// <summary>
/// Risk limits configuration.
/// </summary>
public sealed class RiskLimits
{
    /// <summary>
    /// Gets or sets the maximum daily loss before trading is halted.
    /// </summary>
    public decimal MaxDailyLoss { get; set; } = 500m;

    /// <summary>
    /// Gets or sets the maximum risked amount per individual trade.
    /// </summary>
    public decimal MaxTradeRisk { get; set; } = 100m;

    /// <summary>
    /// Gets or sets the maximum number of trades allowed per day.
    /// </summary>
    public int MaxTradesPerDay { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of concurrent open positions.
    /// </summary>
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
    /// Maximum fraction of equity to allocate to a single position (e.g. 0.05 = 5%).
    /// Used as the equity-cap in PositionSizer's dual formula.
    /// Crypto symbols use fractional quantities (minimum 0.0001, 8 decimal places).
    /// Equity symbols are floored to whole shares with a minimum of 1; if even 1 share
    /// exceeds this cap the signal is blocked at the RISK tier.
    /// </summary>
    public decimal MaxPositionSizePct { get; set; } = 0.05m;

    /// <summary>
    /// Minimum signal confidence threshold (0-1). Signals below this are soft-skipped (FILTER tier).
    /// <para>
    /// Confidence formula: (base + alignmentBoost) × regimeStrength
    /// - Base: 0.8 for trend-aligned, 0.5 for trend-misaligned, 0.2 for ranging
    /// - Alignment boost: +0.1 when slow SMA aligns with signal direction
    /// - Regime strength: 0-1 based on trend strength (2% spread = 1.0)
    /// </para>
    /// <para>
    /// Typical values:
    /// - 0.20: Blocks ranging markets (0.10-0.15), allows most trends (0.375-0.81)
    /// - 0.50: Requires strong trends or aligned setups
    /// - 0.65: Conservative, only high-conviction trend-aligned signals
    /// </para>
    /// </summary>
    public decimal MinSignalConfidence { get; set; } = 0.2m;
}

/// <summary>
/// Exit rules configuration.
/// </summary>
public sealed class ExitOptions
{
    /// <summary>
    /// Gets or sets the interval in seconds between exit checks (default 30 seconds).
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum age in seconds for a price bar returned by GetSnapshotAsync.
    /// Bars older than this threshold cause a MarketDataException (stale price).
    /// 0 disables the check. Default: 300 (5 minutes).
    /// </summary>
    public int MaxPriceAgeSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the exponential backoff base in seconds for failed exit attempts (default 2 seconds).
    /// </summary>
    public int BackoffBaseSeconds { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum backoff cap in seconds (default 300 seconds).
    /// </summary>
    public int BackoffMaxSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the ATR multiplier for stop loss level calculation (default 1.5).
    /// Stop loss = entry price - (ATR * AtrStopLossMultiplier).
    /// </summary>
    public decimal AtrStopLossMultiplier { get; set; } = 1.5m;

    /// <summary>
    /// Gets or sets the ATR multiplier for profit target level calculation (default 3.0).
    /// Profit target = entry price + (ATR * AtrProfitTargetMultiplier).
    /// </summary>
    public decimal AtrProfitTargetMultiplier { get; set; } = 3.0m;

    /// <summary>
    /// Gets or sets the fixed percentage stop loss (default 0.01 = 1%).
    /// </summary>
    public decimal StopLossPercentage { get; set; } = 0.01m;

    /// <summary>
    /// Gets or sets the fixed percentage profit target (default 0.02 = 2%).
    /// </summary>
    public decimal ProfitTargetPercentage { get; set; } = 0.02m;

    /// <summary>
    /// Gets or sets the trailing stop percentage (default 2 = 2%).
    /// </summary>
    public decimal TrailingStopPercent { get; set; } = 2m;
}

/// <summary>
/// Execution configuration.
/// </summary>
public sealed class ExecutionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to run in dry-run mode (no broker submissions).
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the kill switch is enabled.
    /// </summary>
    public bool KillSwitch { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether fractional orders are allowed.
    /// When false (default), all quantities are floored to whole numbers (minimum 1).
    /// Set to true only when the SDK supports fractional read-back correctly.
    /// </summary>
    public bool AllowFractionalOrders { get; set; } = false;

    /// <summary>
    /// Gets or sets the order type for entry orders.
    /// "Market" (default) passes limitPrice=0 to the broker; "AggressiveLimit" uses the bar-close price.
    /// </summary>
    public string EntryOrderType { get; set; } = "Market";

    /// <summary>
    /// Gets or sets the number of bars to fetch per poll cycle for strategy warmup.
    /// Recommended to be >= 51 (SmaCrossoverStrategy.RequiredBars). Default: 100.
    /// </summary>
    public int BarHistoryDepth { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum age in minutes for a bar to be eligible for signal generation.
    /// Bars older than this threshold update indicator history but do not produce signals.
    /// 0 disables the gate. Default: 3 (covers 1-min bar + API latency).
    /// </summary>
    public int MaxBarAgeMinutes { get; set; } = 3;
}

/// <summary>
/// Filters configuration (spread, volume, time-of-day).
/// </summary>
public sealed class FiltersOptions
{
    /// <summary>
    /// Gets or sets the maximum bid/ask spread as a fraction of bid price (default 0.1 = 10%).
    /// </summary>
    public decimal MaxBidAskSpreadPercent { get; set; } = 0.1m;

    /// <summary>
    /// Gets or sets the minimum bar volume required for signal generation (default 100000).
    /// </summary>
    public long MinBarVolume { get; set; } = 100000;

    /// <summary>
    /// Gets or sets the minimum minutes after market open before signals are accepted (default 5).
    /// </summary>
    public int MinMinutesAfterOpen { get; set; } = 5;

    /// <summary>
    /// Gets or sets the minimum minutes before market close during which signals are accepted (default 10).
    /// </summary>
    public int MinMinutesBeforeClose { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum allowed bid/ask spread as a fraction of bid price (default 0.005 = 0.5%).
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
/// Signal quality filter configuration (daily trend bias and volume confirmation).
/// </summary>
public sealed class SignalFilterOptions
{
    /// <summary>
    /// When true, signals are only passed when price is on the correct side of the daily SMA.
    /// Default: false (disabled) — enable via appsettings.json.
    /// </summary>
    public bool EnableDailyTrendFilter { get; set; } = false;

    /// <summary>
    /// SMA period used for the daily trend filter (default: 20).
    /// </summary>
    public int DailySmaPeriod { get; set; } = 20;

    /// <summary>
    /// When true, signals are only passed when current-bar volume exceeds the rolling average.
    /// Default: false (disabled) — enable via appsettings.json.
    /// </summary>
    public bool EnableVolumeFilter { get; set; } = false;

    /// <summary>
    /// Rolling lookback period (in bars) for the volume average (default: 20).
    /// </summary>
    public int VolumeLookbackPeriod { get; set; } = 20;

    /// <summary>
    /// Current volume must be ≥ average × VolumeMultiplier to pass (default: 1.5).
    /// </summary>
    public decimal VolumeMultiplier { get; set; } = 1.5m;
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

/// <summary>
/// Volatility regime options for adaptive sizing and stop behaviour.
/// Uses realised volatility (standard deviation of 1-minute returns) with hysteresis.
/// </summary>
public sealed class VolatilityRegimeOptions
{
    /// <summary>
    /// Enable or disable volatility adaptation.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Number of bars used to estimate realised volatility.
    /// </summary>
    public int LookbackBars { get; set; } = 30;

    /// <summary>
    /// Consecutive classifications required before a regime transition is accepted.
    /// </summary>
    public int TransitionConfirmationBars { get; set; } = 2;

    /// <summary>
    /// Hysteresis buffer applied around thresholds to reduce flip-churn.
    /// e.g. 0.0002 = +/-2 bps of return volatility.
    /// </summary>
    public decimal HysteresisBuffer { get; set; } = 0.0002m;

    /// <summary>
    /// Maximum realised volatility considered Low.
    /// </summary>
    public decimal LowMaxVolatility { get; set; } = 0.003m;

    /// <summary>
    /// Maximum realised volatility considered Normal.
    /// </summary>
    public decimal NormalMaxVolatility { get; set; } = 0.007m;

    /// <summary>
    /// Maximum realised volatility considered High.
    /// Values above this are treated as Extreme.
    /// </summary>
    public decimal HighMaxVolatility { get; set; } = 0.015m;

    /// <summary>
    /// Position-size multiplier in Low volatility.
    /// </summary>
    public decimal LowPositionMultiplier { get; set; } = 1.20m;

    /// <summary>
    /// Position-size multiplier in Normal volatility.
    /// </summary>
    public decimal NormalPositionMultiplier { get; set; } = 1.00m;

    /// <summary>
    /// Position-size multiplier in High volatility.
    /// </summary>
    public decimal HighPositionMultiplier { get; set; } = 0.60m;

    /// <summary>
    /// Position-size multiplier in Extreme volatility.
    /// </summary>
    public decimal ExtremePositionMultiplier { get; set; } = 0.30m;

    /// <summary>
    /// Stop-distance multiplier in Low volatility.
    /// </summary>
    public decimal LowStopMultiplier { get; set; } = 0.80m;

    /// <summary>
    /// Stop-distance multiplier in Normal volatility.
    /// </summary>
    public decimal NormalStopMultiplier { get; set; } = 1.00m;

    /// <summary>
    /// Stop-distance multiplier in High volatility.
    /// </summary>
    public decimal HighStopMultiplier { get; set; } = 1.50m;

    /// <summary>
    /// Stop-distance multiplier in Extreme volatility.
    /// </summary>
    public decimal ExtremeStopMultiplier { get; set; } = 2.00m;

    /// <summary>
    /// Optional equity-specific override profile.
    /// Unset fields inherit from the top-level volatility settings.
    /// </summary>
    public VolatilityRegimeProfileOptions? Equity { get; set; }

    /// <summary>
    /// Optional crypto-specific override profile.
    /// Unset fields inherit from the top-level volatility settings.
    /// </summary>
    public VolatilityRegimeProfileOptions? Crypto { get; set; }
}

/// <summary>
/// Optional per-asset-class overrides for volatility-regime behaviour.
/// Any null property falls back to the top-level <see cref="VolatilityRegimeOptions"/> value.
/// </summary>
public sealed class VolatilityRegimeProfileOptions
{
    /// <summary>
    /// Number of bars used to estimate realised volatility.
    /// </summary>
    public int? LookbackBars { get; set; }

    /// <summary>
    /// Consecutive classifications required before accepting a transition.
    /// </summary>
    public int? TransitionConfirmationBars { get; set; }

    /// <summary>
    /// Hysteresis buffer around thresholds.
    /// </summary>
    public decimal? HysteresisBuffer { get; set; }

    /// <summary>
    /// Maximum realised volatility considered Low.
    /// </summary>
    public decimal? LowMaxVolatility { get; set; }

    /// <summary>
    /// Maximum realised volatility considered Normal.
    /// </summary>
    public decimal? NormalMaxVolatility { get; set; }

    /// <summary>
    /// Maximum realised volatility considered High.
    /// </summary>
    public decimal? HighMaxVolatility { get; set; }

    /// <summary>
    /// Position-size multiplier in Low volatility.
    /// </summary>
    public decimal? LowPositionMultiplier { get; set; }

    /// <summary>
    /// Position-size multiplier in Normal volatility.
    /// </summary>
    public decimal? NormalPositionMultiplier { get; set; }

    /// <summary>
    /// Position-size multiplier in High volatility.
    /// </summary>
    public decimal? HighPositionMultiplier { get; set; }

    /// <summary>
    /// Position-size multiplier in Extreme volatility.
    /// </summary>
    public decimal? ExtremePositionMultiplier { get; set; }

    /// <summary>
    /// Stop-distance multiplier in Low volatility.
    /// </summary>
    public decimal? LowStopMultiplier { get; set; }

    /// <summary>
    /// Stop-distance multiplier in Normal volatility.
    /// </summary>
    public decimal? NormalStopMultiplier { get; set; }

    /// <summary>
    /// Stop-distance multiplier in High volatility.
    /// </summary>
    public decimal? HighStopMultiplier { get; set; }

    /// <summary>
    /// Stop-distance multiplier in Extreme volatility.
    /// </summary>
    public decimal? ExtremeStopMultiplier { get; set; }
}
