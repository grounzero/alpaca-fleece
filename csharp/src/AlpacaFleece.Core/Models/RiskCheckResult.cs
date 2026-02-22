namespace AlpacaFleece.Core.Models;

/// <summary>
/// Result of risk manager checks.
/// </summary>
public record RiskCheckResult(
    bool AllowsSignal,
    string Reason,
    string RiskTier); // "SAFETY", "RISK", "FILTERS"
