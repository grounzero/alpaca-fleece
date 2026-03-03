namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// Base broker exception.
/// </summary>
public class BrokerException : Exception
{
    public BrokerException(string message) : base(message) { }
    public BrokerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Transient broker error (retryable).
/// </summary>
public class BrokerTransientException : BrokerException
{
    public BrokerTransientException(string message) : base(message) { }
    public BrokerTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Timeout error from broker (retryable).
/// </summary>
public class BrokerTimeoutException : BrokerTransientException
{
    public BrokerTimeoutException(string message) : base(message) { }
    public BrokerTimeoutException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Fatal broker error (non-retryable).
/// </summary>
public class BrokerFatalException : BrokerException
{
    public BrokerFatalException(string message) : base(message) { }
    public BrokerFatalException(string message, Exception inner) : base(message, inner) { }
}
