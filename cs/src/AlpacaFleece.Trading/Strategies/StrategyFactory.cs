namespace AlpacaFleece.Trading.Strategies;

/// <summary>
/// Builds a <see cref="StrategyRegistry"/> from the configured <see cref="StrategySelectionOptions"/>.
/// Filters a set of available strategies down to those listed in <c>Active</c>.
/// Logs each registered strategy at startup for auditability.
/// </summary>
public sealed class StrategyFactory(ILogger<StrategyFactory> logger)
{
    /// <summary>
    /// Builds a registry containing only the strategies named in <paramref name="options"/>.
    /// </summary>
    /// <param name="available">
    /// All strategy instances that have been instantiated and wired up.
    /// Each element must implement both <see cref="IStrategy"/> and <see cref="IStrategyMetadata"/>.
    /// </param>
    /// <param name="options">Active strategy names and selection mode from configuration.</param>
    /// <returns>A populated <see cref="StrategyRegistry"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the Active list names a strategy that was not supplied in <paramref name="available"/>,
    /// or when no strategies end up being registered.
    /// </exception>
    public StrategyRegistry Build(
        IEnumerable<(IStrategy Strategy, IStrategyMetadata Metadata)> available,
        StrategySelectionOptions options)
    {
        var registry = new StrategyRegistry();
        var availableByName = available
            .ToDictionary(e => e.Metadata.StrategyName, e => e, StringComparer.OrdinalIgnoreCase);

        // Validate: every Active name must have a matching available strategy
        var missing = options.Active
            .Where(name => !availableByName.ContainsKey(name))
            .ToList();

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Strategy selection error — the following Active strategies were not found: " +
                $"{string.Join(", ", missing)}. " +
                $"Available: {string.Join(", ", availableByName.Keys)}");

        foreach (var name in options.Active.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (strategy, metadata) = availableByName[name];
            registry.Register(strategy, metadata);

            logger.LogInformation(
                "Strategy registered: {Name} v{Version} | Mode={Mode}",
                metadata.StrategyName, metadata.Version, options.Mode);
        }

        if (registry.IsEmpty)
            throw new InvalidOperationException(
                "StrategyFactory produced an empty registry. Check Trading:StrategySelection:Active in configuration.");

        logger.LogInformation(
            "StrategyRegistry built: {Count} strategy(ies) active (Mode={Mode})",
            registry.Count, options.Mode);

        return registry;
    }
}
