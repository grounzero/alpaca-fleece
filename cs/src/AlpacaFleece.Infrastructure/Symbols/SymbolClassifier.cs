// Note: accept plain symbol lists to avoid cross-project references to TradingOptions

namespace AlpacaFleece.Infrastructure.Symbols;

/// <summary>
/// Classifies symbols as crypto or equity based solely on the configured lists.
/// </summary>
public sealed class SymbolClassifier : ISymbolClassifier
{
    private readonly HashSet<string> _cryptoSymbols;
    private readonly HashSet<string> _equitySymbols;

    public SymbolClassifier(IEnumerable<string>? cryptoSymbols = null, IEnumerable<string>? equitySymbols = null)
    {
        var crypto = cryptoSymbols ?? Array.Empty<string>();
        var equity = equitySymbols ?? Array.Empty<string>();

        _cryptoSymbols = new HashSet<string>(crypto, StringComparer.OrdinalIgnoreCase);
        _equitySymbols = new HashSet<string>(equity, StringComparer.OrdinalIgnoreCase);

        // Ensure the same symbol cannot be classified as both crypto and equity.
        var overlappingSymbols = new List<string>();
        foreach (var symbol in _cryptoSymbols)
        {
            if (_equitySymbols.Contains(symbol))
            {
                overlappingSymbols.Add(symbol);
            }
        }

        if (overlappingSymbols.Count > 0)
        {
            throw new ArgumentException(
                $"Symbols cannot be both crypto and equity: {string.Join(", ", overlappingSymbols)}",
                $"{nameof(cryptoSymbols)}, {nameof(equitySymbols)}");
        }
    }

    public bool IsCrypto(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return false;
        return _cryptoSymbols.Contains(symbol);
    }

    public bool IsEquity(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        return _equitySymbols.Contains(symbol);
    }
}
