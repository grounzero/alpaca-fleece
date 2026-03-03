namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// Order manager exception.
/// </summary>
public class OrderManagerException : Exception
{
    public OrderManagerException(string message) : base(message) { }
    public OrderManagerException(string message, Exception inner) : base(message, inner) { }
}
