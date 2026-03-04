namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// State repository exception.
/// </summary>
public class StateRepositoryException : Exception
{
    /// <summary>
    /// Initialises a new instance of the <see cref="StateRepositoryException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public StateRepositoryException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new instance of the <see cref="StateRepositoryException"/> class with a specified error message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public StateRepositoryException(string message, Exception inner) : base(message, inner) { }
}
