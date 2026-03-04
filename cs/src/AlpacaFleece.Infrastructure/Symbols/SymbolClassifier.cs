// Note: accept plain symbol lists to avoid cross-project references to TradingOptions

namespace AlpacaFleece.Infrastructure.Symbols;

/// <summary>
/// Classifies symbols as crypto or equity based solely on the configured lists.
/// </summary>
/// <example>
/// <code>
/// var classifier = new SymbolClassifier(
///     cryptoSymbols: new[] { "BTC/USD", "ETH/USD" },
///     equitySymbols: new[] { "AAPL", "MSFT" }
/// );
/// bool isCrypto = classifier.IsCrypto("BTC/USD"); // true
/// bool isEquity = classifier.IsEquity("AAPL");   // true
/// </code>
/// </example>
public sealed class SymbolClassifier : ISymbolClassifier
{
    private readonly HashSet<string> _cryptoSymbols;
    private readonly HashSet<string> _equitySymbols;

    /// <summary>
    /// Initialises a new instance of the <see cref="SymbolClassifier"/> class with the specified symbol lists.
    /// </summary>
    /// <param name="cryptoSymbols">The list of symbols classified as crypto (case-insensitive). Null defaults to empty list.</param>
    /// <param name="equitySymbols">The list of symbols classified as equity (case-insensitive). Null defaults to empty list.</param>
    /// <exception cref="ArgumentException">Thrown when a symbol appears in both crypto and equity lists.</exception>
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

    /// <summary>
    /// Determines whether the specified symbol is classified as crypto.
    /// </summary>
    /// <param name="symbol">The symbol to check.</param>
    /// <returns>True if the symbol is in the crypto list (case-insensitive); otherwise false.</returns>
    public bool IsCrypto(string symbol) => !string.IsNullOrWhiteSpace(symbol) && _cryptoSymbols.Contains(symbol);

    /// <summary>
    /// Determines whether the specified symbol is classified as equity.
    /// </summary>
    /// <param name="symbol">The symbol to check.</param>
    /// <returns>True if the symbol is in the equity list (case-insensitive); otherwise false.</returns>
    public bool IsEquity(string symbol) => !string.IsNullOrWhiteSpace(symbol) && _equitySymbols.Contains(symbol);
}
