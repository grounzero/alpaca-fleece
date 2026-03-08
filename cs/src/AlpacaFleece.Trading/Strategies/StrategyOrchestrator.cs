namespace AlpacaFleece.Trading.Strategies;

/// <summary>
/// Dispatches incoming bars to all registered strategies.
/// Behaviour is controlled by <see cref="StrategySelectionOptions.Mode"/>:
/// <list type="bullet">
///   <item><term>Single</term><description>Calls the first (and only expected) strategy sequentially.</description></item>
///   <item><term>Multi</term><description>Calls all active strategies concurrently via Task.WhenAll.
///     Per-strategy exceptions are caught and logged so one failing strategy cannot block the others.</description></item>
///   <item><term>Regime</term><description>Filters active strategies to those mapped to the current
///     market regime (via <see cref="RegimeRouter"/>), then dispatches using the same single/parallel
///     path. Falls back to the DEFAULT mapping when regime is unknown (pre-warmup).</description></item>
/// </list>
/// Logs a warning when any dispatch cycle exceeds 100 ms (indicates strategy computation is too slow).
/// </summary>
public sealed class StrategyOrchestrator(
    StrategyRegistry registry,
    TradingOptions tradingOptions,
    ILogger<StrategyOrchestrator> logger,
    RegimeRouter? regimeRouter = null)
{
    // Cache the mode flags once — they are read on every bar.
    private readonly bool _multiMode =
        !tradingOptions.StrategySelection.Mode
            .Equals("Single", StringComparison.OrdinalIgnoreCase);

    private readonly bool _isRegimeMode =
        tradingOptions.StrategySelection.Mode
            .Equals("Regime", StringComparison.OrdinalIgnoreCase);

    private const int SlowDispatchWarningMs = 100;

    /// <summary>
    /// Dispatches <paramref name="bar"/> to all active strategies.
    /// In Regime mode, filters active strategies to those mapped to the detected regime before dispatch.
    /// In Single mode this is a direct await with zero allocation overhead.
    /// In Multi mode all strategies run in parallel; per-strategy failures are isolated.
    /// </summary>
    public async ValueTask DispatchBarAsync(BarEvent bar, CancellationToken ct = default)
    {
        var entries = registry.GetAll();

        if (entries.Count == 0)
        {
            logger.LogWarning("StrategyOrchestrator: no strategies registered — bar skipped for {Symbol}", bar.Symbol);
            return;
        }

        // Regime mode: narrow the dispatched set to strategies mapped for the current regime.
        if (_isRegimeMode && regimeRouter is not null)
        {
            regimeRouter.Update(bar);
            var regime = regimeRouter.GetRegime(bar.Symbol);
            entries = FilterByRegime(entries, regime, tradingOptions.StrategySelection.RegimeMappings);

            if (entries.Count == 0)
            {
                logger.LogWarning(
                    "StrategyOrchestrator: regime {Regime} for {Symbol} has no mapped strategies — bar skipped",
                    regime, bar.Symbol);
                return;
            }

            logger.LogDebug(
                "Regime dispatch: {Symbol} → {Regime} → {Count} strateg(ies)",
                bar.Symbol, regime, entries.Count);
        }

        if (!_multiMode || entries.Count == 1)
        {
            // Single mode or single filtered result: direct call, no overhead.
            await DispatchToStrategyAsync(entries[0], bar, ct);
            return;
        }

        // Multi mode: fan-out in parallel, then wait for all.
        logger.LogDebug(
            "Multi-strategy dispatch: {Symbol} → {Count} strategies (Mode={Mode})",
            bar.Symbol, entries.Count, tradingOptions.StrategySelection.Mode);

        var start = Environment.TickCount64;
        await Task.WhenAll(entries.Select(e => DispatchToStrategyAsync(e, bar, ct)));

        var elapsedMs = Environment.TickCount64 - start;
        if (elapsedMs > SlowDispatchWarningMs)
            logger.LogWarning(
                "StrategyOrchestrator: {Symbol} dispatch took {ElapsedMs}ms across {Count} strategies — consider reducing strategy count or optimising indicators",
                bar.Symbol, elapsedMs, entries.Count);
    }

    // ── private ────────────────────────────────────────────────────────────

    /// <summary>
    /// Filters <paramref name="entries"/> to those whose name appears in the mapping for
    /// <paramref name="regime"/>. When the regime key has no registered matches, falls back to
    /// the DEFAULT mapping. Returns an empty list only when DEFAULT is also unconfigured.
    /// </summary>
    private static IReadOnlyList<(IStrategy Strategy, IStrategyMetadata Metadata)> FilterByRegime(
        IReadOnlyList<(IStrategy Strategy, IStrategyMetadata Metadata)> entries,
        string regime,
        Dictionary<string, List<string>> mappings)
    {
        var names = GetMappedNames(regime, mappings);
        if (names.Count == 0)
            return [];

        var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var result = new List<(IStrategy, IStrategyMetadata)>(2);
        foreach (var entry in entries)
            if (nameSet.Contains(entry.Metadata.StrategyName))
                result.Add(entry);

        // If the specific regime yielded no registered matches, try the DEFAULT list.
        if (result.Count == 0 && !regime.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            var defaults = GetMappedNames("DEFAULT", mappings);
            var defaultSet = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (defaultSet.Contains(entry.Metadata.StrategyName))
                    result.Add(entry);
        }

        return result;
    }

    private static List<string> GetMappedNames(string regime, Dictionary<string, List<string>> mappings)
    {
        if (mappings.TryGetValue(regime, out var names) && names.Count > 0)
            return names;
        if (mappings.TryGetValue("DEFAULT", out var defaults) && defaults.Count > 0)
            return defaults;
        return [];
    }

    private async Task DispatchToStrategyAsync(
        (IStrategy Strategy, IStrategyMetadata Metadata) entry,
        BarEvent bar,
        CancellationToken ct)
    {
        try
        {
            await entry.Strategy.OnBarAsync(bar, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // Always propagate cancellation — do not swallow.
        }
        catch (Exception ex)
        {
            // Isolate the failure: other strategies must still process this bar.
            logger.LogError(ex,
                "Strategy {Name} threw an exception on bar {Symbol} — isolated, other strategies unaffected",
                entry.Metadata.StrategyName, bar.Symbol);
        }
    }
}
