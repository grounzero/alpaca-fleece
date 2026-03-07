namespace AlpacaFleece.AdminUI.Models;

/// <summary>
/// Mutable local copy of the bot's TradingOptions + BrokerOptions used by the config editor.
/// AdminUI does not reference the Trading project, so this mirrors the settings structure.
/// </summary>
public sealed class ConfigDraft
{
    // Broker
    public string ApiKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool IsPaperTrading { get; set; } = true;
    public bool AllowLiveTrading { get; set; } = false;
    public bool KillSwitch { get; set; } = false;
    public bool DryRun { get; set; } = false;

    // Symbols
    public List<string> CryptoSymbols { get; set; } = [];
    public List<string> EquitySymbols { get; set; } = [];
    public long MinVolume { get; set; } = 1000000;

    // Session
    public string TimeZone { get; set; } = "America/New_York";
    public string MarketOpenTime { get; set; } = "09:30";
    public string MarketCloseTime { get; set; } = "16:00";

    // RiskLimits
    public decimal MaxDailyLoss { get; set; } = 2000m;
    public decimal MaxTradeRisk { get; set; } = 500m;
    public int MaxTradesPerDay { get; set; } = 15;
    public int MaxConcurrentPositions { get; set; } = 1;
    public decimal MaxPositionSizePct { get; set; } = 0.02m;
    public decimal MaxRiskPerTradePct { get; set; } = 0.005m;
    public decimal StopLossPct { get; set; } = 0.03m;
    public decimal MinSignalConfidence { get; set; } = 0.2m;

    // Exit
    public decimal AtrStopLossMultiplier { get; set; } = 2.0m;
    public decimal AtrProfitTargetMultiplier { get; set; } = 3.0m;
    public decimal TrailingStopPercent { get; set; } = 1.0m;
    public int ExitCheckIntervalSeconds { get; set; } = 15;
    public int MaxPriceAgeSeconds { get; set; } = 300;

    // Execution
    public int BarHistoryDepth { get; set; } = 250;
    public int MaxBarAgeMinutes { get; set; } = 5;
    public bool AllowFractionalOrders { get; set; } = false;
    public string EntryOrderType { get; set; } = "Market";

    // Filters
    public decimal MaxBidAskSpreadPercent { get; set; } = 0.05m;
    public long MinBarVolume { get; set; } = 500000;
    public int MinMinutesAfterOpen { get; set; } = 10;
    public int MinMinutesBeforeClose { get; set; } = 15;

    // Drawdown
    public bool DrawdownEnabled { get; set; } = true;
    public int LookbackDays { get; set; } = 20;
    public bool EnableAutoRecovery { get; set; } = true;
    public decimal WarningThresholdPct { get; set; } = 0.02m;
    public decimal HaltThresholdPct { get; set; } = 0.04m;
    public decimal EmergencyThresholdPct { get; set; } = 0.08m;
    public decimal WarningPositionMultiplier { get; set; } = 0.25m;

    // Signal Filters
    public bool EnableDailyTrendFilter { get; set; } = false;
    public int DailySmaPeriod { get; set; } = 50;
    public bool EnableVolumeFilter { get; set; } = false;
    public int VolumeLookbackPeriod { get; set; } = 30;
    public decimal VolumeMultiplier { get; set; } = 2.0m;

    // Correlation
    public bool CorrelationEnabled { get; set; } = true;
    public decimal MaxCorrelation { get; set; } = 0.65m;
    public decimal MaxSectorPct { get; set; } = 0.15m;
    public decimal MaxAssetClassPct { get; set; } = 0.30m;

    // Volatility regime (global)
    public bool VolatilityRegimeEnabled { get; set; } = false;
    public int VolatilityLookbackBars { get; set; } = 30;
    public int VolatilityTransitionConfirmationBars { get; set; } = 2;
    public decimal VolatilityHysteresisBuffer { get; set; } = 0.0002m;
    public decimal VolatilityLowMaxVolatility { get; set; } = 0.003m;
    public decimal VolatilityNormalMaxVolatility { get; set; } = 0.007m;
    public decimal VolatilityHighMaxVolatility { get; set; } = 0.015m;
    public decimal VolatilityLowPositionMultiplier { get; set; } = 1.20m;
    public decimal VolatilityNormalPositionMultiplier { get; set; } = 1.00m;
    public decimal VolatilityHighPositionMultiplier { get; set; } = 0.60m;
    public decimal VolatilityExtremePositionMultiplier { get; set; } = 0.30m;
    public decimal VolatilityLowStopMultiplier { get; set; } = 0.80m;
    public decimal VolatilityNormalStopMultiplier { get; set; } = 1.00m;
    public decimal VolatilityHighStopMultiplier { get; set; } = 1.50m;
    public decimal VolatilityExtremeStopMultiplier { get; set; } = 2.00m;

    // Volatility regime (equity overrides)
    public bool UseEquityVolatilityOverrides { get; set; } = false;
    public int EquityVolatilityLookbackBars { get; set; } = 30;
    public int EquityVolatilityTransitionConfirmationBars { get; set; } = 2;
    public decimal EquityVolatilityHysteresisBuffer { get; set; } = 0.0002m;
    public decimal EquityVolatilityLowMaxVolatility { get; set; } = 0.003m;
    public decimal EquityVolatilityNormalMaxVolatility { get; set; } = 0.007m;
    public decimal EquityVolatilityHighMaxVolatility { get; set; } = 0.015m;
    public decimal EquityVolatilityLowPositionMultiplier { get; set; } = 1.20m;
    public decimal EquityVolatilityNormalPositionMultiplier { get; set; } = 1.00m;
    public decimal EquityVolatilityHighPositionMultiplier { get; set; } = 0.60m;
    public decimal EquityVolatilityExtremePositionMultiplier { get; set; } = 0.30m;
    public decimal EquityVolatilityLowStopMultiplier { get; set; } = 0.80m;
    public decimal EquityVolatilityNormalStopMultiplier { get; set; } = 1.00m;
    public decimal EquityVolatilityHighStopMultiplier { get; set; } = 1.50m;
    public decimal EquityVolatilityExtremeStopMultiplier { get; set; } = 2.00m;

    // Volatility regime (crypto overrides)
    public bool UseCryptoVolatilityOverrides { get; set; } = false;
    public int CryptoVolatilityLookbackBars { get; set; } = 30;
    public int CryptoVolatilityTransitionConfirmationBars { get; set; } = 2;
    public decimal CryptoVolatilityHysteresisBuffer { get; set; } = 0.0002m;
    public decimal CryptoVolatilityLowMaxVolatility { get; set; } = 0.003m;
    public decimal CryptoVolatilityNormalMaxVolatility { get; set; } = 0.007m;
    public decimal CryptoVolatilityHighMaxVolatility { get; set; } = 0.015m;
    public decimal CryptoVolatilityLowPositionMultiplier { get; set; } = 1.20m;
    public decimal CryptoVolatilityNormalPositionMultiplier { get; set; } = 1.00m;
    public decimal CryptoVolatilityHighPositionMultiplier { get; set; } = 0.60m;
    public decimal CryptoVolatilityExtremePositionMultiplier { get; set; } = 0.30m;
    public decimal CryptoVolatilityLowStopMultiplier { get; set; } = 0.80m;
    public decimal CryptoVolatilityNormalStopMultiplier { get; set; } = 1.00m;
    public decimal CryptoVolatilityHighStopMultiplier { get; set; } = 1.50m;
    public decimal CryptoVolatilityExtremeStopMultiplier { get; set; } = 2.00m;

    public bool IsDirty { get; set; } = false;
}

/// <summary>Represents a single changed field for the preview-changes modal.</summary>
public sealed record ConfigChange(
    string Field,
    string OldValue,
    string NewValue,
    bool IsRiskIncrease);
