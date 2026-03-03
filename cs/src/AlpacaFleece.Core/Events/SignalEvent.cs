namespace AlpacaFleece.Core.Events;

/// <summary>
/// Emitted when strategy identifies a signal (entry or reversal).
/// </summary>
public sealed record SignalEvent(
    string Symbol,
    string Side, // "BUY" or "SELL"
    string Timeframe,
    DateTimeOffset SignalTimestamp,
    SignalMetadata Metadata) : IEvent;

/// <summary>
/// Metadata attached to a signal for audit and decision tracking.
/// Contains SMA periods, fast/medium/slow SMAs, ATR, confidence, and regime info.
/// </summary>
public sealed record SignalMetadata(
    (int Fast, int Slow) SmaPeriod,
    decimal FastSma,
    decimal MediumSma,
    decimal SlowSma,
    decimal? Atr, // FIX FOR #35: ATR computed and stored here
    decimal Confidence, // 0-1 confidence score
    string Regime, // TRENDING_UP, TRENDING_DOWN, RANGING
    decimal RegimeStrength,
    decimal CurrentPrice,
    // Legacy fields for backwards compatibility
    decimal AtrValue = 0m,
    string RegimeType = "",
    int BarsInRegime = 0);
