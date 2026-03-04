namespace AlpacaFleece.Core.Interfaces;

/// <summary>Classifies trading symbols as crypto or equity.</summary>
public interface ISymbolClassifier
{
    /// <summary>Returns true if the symbol is a cryptocurrency.</summary>
    bool IsCrypto(string symbol);

    /// <summary>Returns true if the symbol is an equity.</summary>
    bool IsEquity(string symbol);
}
