namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// State repository exception.
/// </summary>
public class StateRepositoryException : Exception
{
    public StateRepositoryException(string message) : base(message) { }
    public StateRepositoryException(string message, Exception inner) : base(message, inner) { }
}
