namespace AlpacaFleece.Core.Exceptions;

/// <summary>
/// Risk manager exception (SAFETY or RISK tier violations).
/// </summary>
public class RiskManagerException : Exception
{
    public RiskManagerException(string message) : base(message) { }
    public RiskManagerException(string message, Exception inner) : base(message, inner) { }
}
