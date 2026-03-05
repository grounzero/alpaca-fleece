namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// Risk manager exception (SAFETY or RISK tier violations).
/// </summary>
public class RiskManagerException : Exception
{
    /// <summary>
    /// Initialises a new instance of the <see cref="RiskManagerException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public RiskManagerException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new instance of the <see cref="RiskManagerException"/> class with a specified error message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public RiskManagerException(string message, Exception inner) : base(message, inner) { }
}
