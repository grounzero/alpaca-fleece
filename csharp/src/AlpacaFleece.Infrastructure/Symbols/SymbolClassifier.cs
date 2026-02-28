using AlpacaFleece.Core.Interfaces;
// Note: accept plain symbol lists to avoid cross-project references to TradingOptions

namespace AlpacaFleece.Infrastructure.Symbols;

/// <summary>
/// Classifies symbols as crypto or equity based on configured lists.
/// Includes a temporary fallback to slash-based detection if lists are empty.
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
