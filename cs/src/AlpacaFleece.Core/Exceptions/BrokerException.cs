namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// Base broker exception.
/// </summary>
public class BrokerException : Exception
{
    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public BrokerException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerException"/> class with a specified error message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public BrokerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Transient broker error (retryable).
/// </summary>
public class BrokerTransientException : BrokerException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerTransientException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public BrokerTransientException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerTransientException"/> class with a specified error message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public BrokerTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Timeout error from broker (retryable).
/// </summary>
public class BrokerTimeoutException : BrokerTransientException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerTimeoutException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public BrokerTimeoutException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerTimeoutException"/> class with a specified error message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public BrokerTimeoutException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Fatal broker error (non-retryable).
/// </summary>
public class BrokerFatalException : BrokerException
{
    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerFatalException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public BrokerFatalException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new instance of the <see cref="BrokerFatalException"/> class with a specified error message and an inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public BrokerFatalException(string message, Exception inner) : base(message, inner) { }
}
