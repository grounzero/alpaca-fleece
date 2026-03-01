namespace AlpacaFleece.Core.Interfaces;

public interface ISymbolClassifier
{
    bool IsCrypto(string symbol);
    bool IsEquity(string symbol);
}
