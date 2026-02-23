namespace AlpacaFleece.Trading.Risk;

/// <summary>
/// Checks portfolio correlation and concentration limits before accepting new signals.
///
/// Three checks (all soft-skip, FILTER tier):
///   1. Pairwise correlation  — rejects if any existing position has correlation > MaxCorrelation
///   2. Sector concentration  — rejects if adding the symbol would exceed MaxSectorPct of capacity
///   3. Asset class concentration — rejects if adding the symbol would exceed MaxAssetClassPct
///
/// Correlation values come from CorrelationLimits.StaticCorrelations in config.
/// Sector and asset-class mappings come from SectorMapping.
/// All checks are synchronous (in-memory only) — well within the 100ms budget.
/// </summary>
public sealed class CorrelationService(
    TradingOptions options,
    IPositionTracker positionTracker,
    ILogger<CorrelationService> logger)
{
    /// <summary>
    /// Returns a passing RiskCheckResult if the new symbol clears all correlation and
    /// concentration checks. Returns AllowsSignal=false (soft skip) on any breach.
    /// </summary>
    public RiskCheckResult Check(string newSymbol)
    {
        var cfg = options.CorrelationLimits;
        if (!cfg.Enabled)
            return new RiskCheckResult(true, "Correlation limits disabled", "FILTER");

        // Exclude same symbol — reversal scenario, already counted in portfolio.
        var others = positionTracker.GetAllPositions().Keys
            .Where(s => !string.Equals(s, newSymbol, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (others.Count == 0)
            return new RiskCheckResult(true, "Correlation checks passed", "FILTER");

        // ── 1. Pairwise correlation ───────────────────────────────────────────────
        foreach (var existing in others)
        {
            var corr = GetCorrelation(newSymbol, existing);
            if (corr > cfg.MaxCorrelation)
            {
                var msg = $"Correlation {newSymbol}:{existing} = {corr:F2} exceeds limit {cfg.MaxCorrelation:F2}";
                logger.LogInformation("Correlation filter block: {Message}", msg);
                return new RiskCheckResult(false, msg, "FILTER");
            }
        }

        var maxPositions = options.RiskLimits.MaxConcurrentPositions;
        if (maxPositions > 0)
        {
            // ── 2. Sector concentration ───────────────────────────────────────────
            var newSector = SectorMapping.GetSector(newSymbol);
            if (newSector != SectorMapping.UnknownSector)
            {
                var sectorCount = others.Count(s => SectorMapping.GetSector(s) == newSector) + 1;
                var sectorPct = (decimal)sectorCount / maxPositions;
                if (sectorPct > cfg.MaxSectorPct)
                {
                    var msg = $"Sector '{newSector}' would reach {sectorPct:P0} of capacity, " +
                              $"limit is {cfg.MaxSectorPct:P0}";
                    logger.LogInformation("Sector concentration filter block: {Message}", msg);
                    return new RiskCheckResult(false, msg, "FILTER");
                }
            }

            // ── 3. Asset class concentration ──────────────────────────────────────
            var newClass = SectorMapping.GetAssetClass(newSymbol);
            var classCount = others.Count(s => SectorMapping.GetAssetClass(s) == newClass) + 1;
            var classPct = (decimal)classCount / maxPositions;
            if (classPct > cfg.MaxAssetClassPct)
            {
                var msg = $"Asset class '{newClass}' would reach {classPct:P0} of capacity, " +
                          $"limit is {cfg.MaxAssetClassPct:P0}";
                logger.LogInformation("Asset class concentration filter block: {Message}", msg);
                return new RiskCheckResult(false, msg, "FILTER");
            }
        }

        return new RiskCheckResult(true, "Correlation checks passed", "FILTER");
    }

    /// <summary>
    /// Looks up the static correlation between two symbols.
    /// Tries both orderings of the "A:B" key to be config-friendly.
    /// Returns 0 (assumed uncorrelated) if the pair is not configured.
    /// </summary>
    private decimal GetCorrelation(string a, string b)
    {
        var correlations = options.CorrelationLimits.StaticCorrelations;
        if (correlations.TryGetValue($"{a}:{b}", out var corr1)) return corr1;
        if (correlations.TryGetValue($"{b}:{a}", out var corr2)) return corr2;
        return 0m;
    }
}
