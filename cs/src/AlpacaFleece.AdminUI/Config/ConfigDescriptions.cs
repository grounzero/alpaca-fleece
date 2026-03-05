namespace AlpacaFleece.AdminUI.Config;

/// <summary>
/// Field descriptions shown in the config editor's info popups.
/// Key format: "Section.Field".
/// </summary>
public static class ConfigDescriptions
{
    public static readonly IReadOnlyDictionary<string, string> All =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RiskLimits.MaxRiskPerTradePct"] =
                "Percentage of equity risked per trade. Drives position size formula: qty = equity × risk% ÷ (price × stopLoss%)",
            ["RiskLimits.MaxDailyLoss"] =
                "Maximum dollar loss allowed today before all trading halts. Resets at market open.",
            ["RiskLimits.MaxTradeRisk"] =
                "Maximum dollar amount risked on a single trade entry.",
            ["RiskLimits.MaxTradesPerDay"] =
                "Maximum number of new trade entries per trading day.",
            ["RiskLimits.MaxConcurrentPositions"] =
                "Maximum number of open positions at any one time.",
            ["RiskLimits.MaxPositionSizePct"] =
                "Maximum percentage of portfolio equity allocated to a single position.",
            ["RiskLimits.StopLossPct"] =
                "Fixed stop-loss distance as a percentage of entry price (used when ATR is unavailable).",
            ["RiskLimits.MinSignalConfidence"] =
                "Minimum strategy confidence score (0–1) required before a signal proceeds to risk checks.",
            ["Drawdown.WarningThresholdPct"] =
                "Drawdown percentage at which position size is halved (Warning level). Triggered when portfolio falls this far below its peak.",
            ["Drawdown.HaltThresholdPct"] =
                "Drawdown percentage at which no new positions are opened (Halt level). Existing positions are held.",
            ["Drawdown.EmergencyThresholdPct"] =
                "Drawdown percentage at which all positions are flattened and trading stops (Emergency level).",
            ["Drawdown.WarningPositionMultiplier"] =
                "Position size multiplier applied when drawdown is in Warning state (e.g. 0.25 = 25% of normal size).",
            ["Drawdown.LookbackDays"] =
                "Rolling window in calendar days used to compute the peak equity for drawdown calculations.",
            ["Drawdown.Enabled"] =
                "Enable or disable the drawdown monitor entirely. When disabled, no drawdown-based limits apply.",
            ["Drawdown.EnableAutoRecovery"] =
                "When enabled, the bot automatically recovers from Warning/Halt states as equity recovers. When disabled, manual recovery is required.",
            ["Exit.AtrStopLossMultiplier"] =
                "Stop loss is set this many ATR multiples below entry price. Higher = wider stop, fewer premature exits.",
            ["Exit.AtrProfitTargetMultiplier"] =
                "Profit target is set this many ATR multiples above entry price. Should be greater than AtrStopLossMultiplier for positive R:R.",
            ["Exit.TrailingStopPercent"] =
                "Trailing stop distance as a percentage of the highest price reached since entry.",
            ["Exit.CheckIntervalSeconds"] =
                "How frequently (in seconds) the exit manager checks all open positions for stop/target hits.",
            ["Exit.MaxPriceAgeSeconds"] =
                "Maximum acceptable age of a price quote in seconds. If the latest price is stale, the exit check is skipped (0 = disabled).",
            ["Execution.EntryOrderType"] =
                "Order type for new entries: Market (fills immediately at market price) or Limit (fills at specified price or better).",
            ["Execution.MaxBarAgeMinutes"] =
                "Maximum acceptable age of a bar in minutes. Stale bars update indicators but do not generate signals (0 = disabled).",
            ["Execution.AllowFractionalOrders"] =
                "Allow fractional share quantities for equities. Required for low-priced positions with precise risk sizing.",
            ["Execution.BarHistoryDepth"] =
                "Number of historical bars loaded on startup for indicator warm-up. Minimum 51 required.",
            ["Filters.MaxBidAskSpreadPercent"] =
                "Maximum acceptable bid-ask spread as a percentage of mid price. Wide spreads indicate illiquid conditions.",
            ["SignalFilters.EnableDailyTrendFilter"] =
                "Only take long signals when the daily close is above its SMA. Reduces counter-trend entries.",
            ["SignalFilters.EnableVolumeFilter"] =
                "Only take signals when current bar volume exceeds the rolling average by the configured multiplier.",
            ["Correlation.MaxCorrelation"] =
                "Maximum pairwise correlation allowed between a new position and any existing position.",
            ["Correlation.MaxSectorPct"] =
                "Maximum percentage of max concurrent positions that may be in the same GICS sector.",
            ["Broker.KillSwitch"] =
                "Emergency kill switch. When true, no new orders are submitted and existing positions may be flattened.",
            ["Broker.DryRun"] =
                "Dry-run mode. Orders are logged but not submitted to the broker. Useful for testing strategy logic.",
        };
}
