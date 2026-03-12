namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Provides identity and version information for a strategy plugin.
/// Implement alongside <see cref="IStrategy"/> to enable registry discovery
/// and signal attribution in multi-strategy mode.
/// </summary>
public interface IStrategyMetadata
{
    /// <summary>
    /// Gets the unique, stable identifier for this strategy.
    /// Used as the key in <see cref="StrategyRegistry"/> and as
    /// the value stored on <see cref="Events.SignalEvent.StrategyName"/>.
    /// Must not change once deployed — changing it orphans historical order intents.
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// Gets the semantic version of this strategy implementation (e.g. "2.1.0").
    /// Logged at startup; useful for correlating behavioural changes to code deployments.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Gets an optional human-readable description of the strategy.
    /// </summary>
    string? Description { get; }
}
