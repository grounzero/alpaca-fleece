namespace AlpacaFleece.Trading.Strategies;

/// <summary>
/// Holds all active strategy instances keyed by <see cref="IStrategyMetadata.StrategyName"/>.
/// Built once at startup by <see cref="StrategyFactory"/> and registered as a singleton.
/// Thread-safe for concurrent reads (populated during startup only).
/// </summary>
public sealed class StrategyRegistry
{
    private readonly Dictionary<string, (IStrategy Strategy, IStrategyMetadata Metadata)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the number of registered strategies.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Gets a value indicating whether the registry has no strategies.
    /// </summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// Registers a strategy. Throws if a strategy with the same name is already registered.
    /// </summary>
    /// <exception cref="InvalidOperationException">Duplicate strategy name.</exception>
    public void Register(IStrategy strategy, IStrategyMetadata metadata)
    {
        if (_entries.ContainsKey(metadata.StrategyName))
            throw new InvalidOperationException(
                $"Strategy '{metadata.StrategyName}' is already registered. Strategy names must be unique.");

        _entries[metadata.StrategyName] = (strategy, metadata);
    }

    /// <summary>
    /// Returns the strategy instance for the given name, or null if not found.
    /// </summary>
    public IStrategy? Get(string strategyName)
        => _entries.TryGetValue(strategyName, out var entry) ? entry.Strategy : null;

    /// <summary>
    /// Returns all registered strategy entries in registration order.
    /// </summary>
    public IReadOnlyList<(IStrategy Strategy, IStrategyMetadata Metadata)> GetAll()
        => [.. _entries.Values];
}
