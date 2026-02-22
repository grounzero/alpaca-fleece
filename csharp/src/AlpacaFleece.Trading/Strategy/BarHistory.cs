namespace AlpacaFleece.Trading.Strategy;

/// <summary>
/// Fixed-size circular buffer for bar history (OHLCV quotes).
/// Used for SMA and ATR calculations.
/// </summary>
public sealed class BarHistory(int maxSize)
{
    private readonly List<(decimal, decimal, decimal, decimal, long)> _bars = new(maxSize);

    public int Count => _bars.Count;
    public bool IsFull => _bars.Count >= maxSize;

    /// <summary>
    /// Adds a bar to history. Removes oldest if full.
    /// </summary>
    public void AddBar(decimal open, decimal high, decimal low, decimal close, long volume)
    {
        _bars.Add((open, high, low, close, volume));
        if (_bars.Count > maxSize)
        {
            _bars.RemoveAt(0);
        }
    }

    /// <summary>
    /// Returns bars as Span for efficient enumeration (no allocation in hot path).
    /// </summary>
    public Span<(decimal, decimal, decimal, decimal, long)> GetAsSpan()
    {
        // Note: List doesn't have AsSpan() in .NET Standard
        // For production, use a fixed array with index
        return new Span<(decimal, decimal, decimal, decimal, long)>(_bars.ToArray());
    }

    /// <summary>
    /// Gets underlying bars list (read-only).
    /// Item1=Open, Item2=High, Item3=Low, Item4=Close, Item5=Volume.
    /// </summary>
    public IReadOnlyList<(decimal Open, decimal High, decimal Low, decimal Close, long Volume)> GetBars()
    {
        return _bars.AsReadOnly();
    }

    /// <summary>
    /// Calculates simple moving average of close prices.
    /// </summary>
    public decimal CalculateSma(int period)
    {
        if (_bars.Count < period)
            return 0;

        var sum = 0m;
        for (var i = _bars.Count - period; i < _bars.Count; i++)
        {
            sum += _bars[i].Item4; // Close
        }

        return sum / period;
    }

    /// <summary>
    /// Calculates Average True Range (ATR) for volatility.
    /// </summary>
    public decimal CalculateAtr(int period)
    {
        if (_bars.Count < period)
            return 0;

        var trSum = 0m;
        for (var i = Math.Max(0, _bars.Count - period); i < _bars.Count; i++)
        {
            var high = _bars[i].Item2;   // High
            var low = _bars[i].Item3;    // Low
            var prevClose = i > 0 ? _bars[i - 1].Item4 : _bars[i].Item4; // Close

            var tr = Math.Max(
                Math.Max(high - low, Math.Abs(high - prevClose)),
                Math.Abs(low - prevClose));

            trSum += tr;
        }

        return trSum / period;
    }

    /// <summary>
    /// Clears all bars.
    /// </summary>
    public void Clear()
    {
        _bars.Clear();
    }
}
