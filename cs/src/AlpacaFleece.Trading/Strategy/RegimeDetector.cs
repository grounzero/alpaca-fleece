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
    private readonly int _transitionConfirmationBars;
    private readonly Dictionary<string, RegimeState> _states = new(StringComparer.OrdinalIgnoreCase);

    private sealed class RegimeState
    {
        public string CurrentRegime { get; set; } = "RANGING";
        public int BarsInRegime { get; set; }
        public string? PendingRegime { get; set; }
        public int PendingBars { get; set; }
    }

    public RegimeDetector(int transitionConfirmationBars = 2)
    {
        _transitionConfirmationBars = Math.Max(1, transitionConfirmationBars);
    }

    /// <summary>
    /// Stateful, per-symbol regime detection with transition confirmation.
    /// Prevents one-bar oscillations from flipping the effective regime.
    /// </summary>
    public RegimeScore DetectRegime(string symbol, decimal fast, decimal medium, decimal slow)
    {
        var (rawRegime, strength) = DetectRawRegime(fast, medium, slow);

        if (!_states.TryGetValue(symbol, out var state))
        {
            state = new RegimeState
            {
                CurrentRegime = rawRegime,
                BarsInRegime = 1,
                PendingRegime = null,
                PendingBars = 0
            };
            _states[symbol] = state;
            return new RegimeScore(state.CurrentRegime, state.BarsInRegime, strength);
        }

        if (rawRegime == state.CurrentRegime)
        {
            state.BarsInRegime++;
            state.PendingRegime = null;
            state.PendingBars = 0;
            return new RegimeScore(state.CurrentRegime, state.BarsInRegime, strength);
        }

        if (state.PendingRegime == rawRegime)
            state.PendingBars++;
        else
        {
            state.PendingRegime = rawRegime;
            state.PendingBars = 1;
        }

        if (state.PendingBars >= _transitionConfirmationBars)
        {
            state.CurrentRegime = rawRegime;
            state.BarsInRegime = 1;
            state.PendingRegime = null;
            state.PendingBars = 0;
            return new RegimeScore(state.CurrentRegime, state.BarsInRegime, strength);
        }

        // Transition not confirmed yet: keep the current regime stable.
        state.BarsInRegime++;
        return new RegimeScore(state.CurrentRegime, state.BarsInRegime, strength);
    }

    /// <summary>
    /// Detects regime based on three SMA levels.
    /// If slowest > middle > fastest = TRENDING_DOWN (bearish alignment)
    /// If fastest > middle > slowest = TRENDING_UP (bullish alignment)
    /// Otherwise = RANGING
    /// </summary>
    public RegimeScore DetectRegime(decimal fast, decimal medium, decimal slow)
    {
        var (regime, strength) = DetectRawRegime(fast, medium, slow);
        return regime == "RANGING"
            ? new RegimeScore(regime, 0, strength)
            : new RegimeScore(regime, 1, strength);
    }

    private static (string Regime, decimal Strength) DetectRawRegime(decimal fast, decimal medium, decimal slow)
    {
        // Trending up: fastest > medium > slowest (bullish alignment)
        if (fast > medium && medium > slow)
        {
            // Strength based on spread between fastest and slowest
            var spread = fast - slow;
            var strength = slow > 0 ? Math.Min(1m, spread / (slow * 0.02m)) : 0.5m;
            return ("TRENDING_UP", Math.Min(1m, strength));
        }

        // Trending down: slowest > medium > fastest (bearish alignment)
        if (fast < medium && medium < slow)
        {
            // Strength based on spread between slowest and fastest
            var spread = slow - fast;
            var strength = slow > 0 ? Math.Min(1m, spread / (slow * 0.02m)) : 0.5m;
            return ("TRENDING_DOWN", Math.Min(1m, strength));
        }

        // Ranging: SMAs are not aligned
        return ("RANGING", 0.5m);
    }
}
