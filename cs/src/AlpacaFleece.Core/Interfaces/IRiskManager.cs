namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Risk manager interface for signal vetting (3-tier: safety, risk, filters).
/// </summary>
public interface IRiskManager
{
    /// <summary>
    /// Checks if a signal should be allowed based on risk rules.
    /// Returns RiskCheckResult with allow flag and reason.
    /// </summary>
    ValueTask<RiskCheckResult> CheckSignalAsync(
        SignalEvent signal,
        CancellationToken ct = default);
}
