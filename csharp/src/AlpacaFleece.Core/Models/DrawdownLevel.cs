namespace AlpacaFleece.Core.Models;

/// <summary>
/// Drawdown escalation levels, ordered from least to most severe.
/// </summary>
public enum DrawdownLevel
{
    /// <summary>
    /// Normal operation — drawdown below warning threshold.
    /// </summary>
    Normal,

    /// <summary>
    /// Warning — drawdown between 3% and 5% of peak equity.
    /// Position sizes are reduced by the configured multiplier (default 50%).
    /// </summary>
    Warning,

    /// <summary>
    /// Halt — drawdown between 5% and 10% of peak equity.
    /// No new positions; existing positions held.
    /// </summary>
    Halt,

    /// <summary>
    /// Emergency — drawdown exceeds 10% of peak equity.
    /// All positions closed immediately.
    /// </summary>
    Emergency
}
