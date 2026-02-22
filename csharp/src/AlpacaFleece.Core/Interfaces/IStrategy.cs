namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Strategy interface for signal generation.
/// </summary>
public interface IStrategy
{
    /// <summary>
    /// Processes incoming bar and emits signals if conditions met.
    /// </summary>
    ValueTask OnBarAsync(BarEvent bar, CancellationToken ct = default);

    /// <summary>
    /// Returns the required history length (number of bars) before strategy is ready.
    /// </summary>
    int RequiredHistory { get; }

    /// <summary>
    /// Returns true if strategy has enough history to generate signals.
    /// </summary>
    bool IsReady { get; }
}
