namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// Strategy exception.
/// </summary>
public class StrategyException : Exception
{
    public StrategyException(string message) : base(message) { }
    public StrategyException(string message, Exception inner) : base(message, inner) { }
}
