namespace AlpacaFleece.Trading.Strategy;

/// <summary>
/// Regime detection: trending vs ranging.
/// </summary>
public record RegimeScore(
    string RegimeType, // "TRENDING_UP", "TRENDING_DOWN", "RANGING"
    int BarsInRegime,
    decimal Strength); // 0-1 confidence

/// <summary>
/// Detects market regime using SMA alignment.
/// Uses fast, medium, slow SMAs to determine trend direction and strength.
/// </summary>
public sealed class RegimeDetector
{
    /// <summary>
    /// Detects regime based on three SMA levels.
    /// If slowest > middle > fastest = TRENDING_DOWN (bearish alignment)
    /// If fastest > middle > slowest = TRENDING_UP (bullish alignment)
    /// Otherwise = RANGING
    /// </summary>
    public RegimeScore DetectRegime(decimal fast, decimal medium, decimal slow)
    {
        // Trending up: fastest > medium > slowest (bullish alignment)
        if (fast > medium && medium > slow)
        {
            // Strength based on spread between fastest and slowest
            var spread = fast - slow;
            var strength = slow > 0 ? Math.Min(1m, spread / (slow * 0.02m)) : 0.5m;
            return new RegimeScore("TRENDING_UP", 1, Math.Min(1m, strength));
        }

        // Trending down: slowest > medium > fastest (bearish alignment)
        if (fast < medium && medium < slow)
        {
            // Strength based on spread between slowest and fastest
            var spread = slow - fast;
            var strength = slow > 0 ? Math.Min(1m, spread / (slow * 0.02m)) : 0.5m;
            return new RegimeScore("TRENDING_DOWN", 1, Math.Min(1m, strength));
        }

        // Ranging: SMAs are not aligned
        return new RegimeScore("RANGING", 0, 0.5m);
    }
}
