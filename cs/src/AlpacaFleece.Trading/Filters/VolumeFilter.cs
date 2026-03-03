namespace AlpacaFleece.Trading.Filters;

/// <summary>
/// Filters signals based on volume relative to a rolling average.
/// Current bar volume must be ≥ average × VolumeMultiplier to pass.
/// </summary>
public sealed class VolumeFilter(
    TradingOptions options,
    ILogger<VolumeFilter> logger)
{
    /// <summary>
    /// Returns true if current volume exceeds the rolling-average threshold, or if filter is disabled.
    /// Returns true (pass) on insufficient history — safe fallback, never blocks on missing data.
    /// </summary>
    public bool Check(IReadOnlyList<(decimal Open, decimal High, decimal Low, decimal Close, long Volume)> bars)
    {
        if (!options.SignalFilters.EnableVolumeFilter)
            return true;

        // Minimum 2: with lookback=1 the average equals the current bar's volume, so
        // currentVolume >= avg * multiplier (default 1.5) can never be true — every signal blocked.
        var lookback = Math.Max(2, options.SignalFilters.VolumeLookbackPeriod);
        if (bars.Count < lookback)
            return true;

        var currentVolume = bars[^1].Volume;

        var sum = 0L;
        for (var i = bars.Count - lookback; i < bars.Count; i++)
            sum += bars[i].Volume;

        var avg = (decimal)sum / lookback;
        var threshold = avg * options.SignalFilters.VolumeMultiplier;
        var passes = currentVolume >= threshold;

        if (!passes)
            logger.LogDebug(
                "VolumeFilter: blocked — volume={Volume} vs threshold={Threshold:F0} (avg={Avg:F0} × {Mult})",
                currentVolume, threshold, avg, options.SignalFilters.VolumeMultiplier);

        return passes;
    }
}
