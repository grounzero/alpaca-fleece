namespace AlpacaFleece.Trading.Orders;

/// <summary>
/// Deterministic SHA-256 client order ID generator.
/// Input: strategy:symbol:timeframe:signalTs.isoformat():side
/// Output: SHA256 hex lowercase, first 16 chars
/// Ensures idempotency across restarts and duplicate signal detection.
/// </summary>
public sealed class OrderIdGenerator
{
    /// <summary>
    /// Generates a deterministic SHA-256 client order ID from signal components.
    /// </summary>
    /// <param name="strategy">Strategy name (e.g., "sma_crossover_multi")</param>
    /// <param name="symbol">Trading symbol (e.g., "AAPL")</param>
    /// <param name="timeframe">Timeframe (e.g., "1Min", "5Min")</param>
    /// <param name="signalTimestamp">Signal timestamp in UTC (ISO8601 format)</param>
    /// <param name="side">Order side ("buy" or "sell", lowercase)</param>
    /// <returns>First 16 hex characters of SHA256 hash</returns>
    public static string GenerateClientOrderId(
        string strategy,
        string symbol,
        string timeframe,
        DateTimeOffset signalTimestamp,
        string side)
    {
        // Construct input string in exact format: strategy:symbol:timeframe:signalTs.isoformat():side
        // ISO8601 format: "2024-02-21T14:30:00.000+00:00"
        var input = $"{strategy}:{symbol}:{timeframe}:{signalTimestamp:O}:{side}";

        // Compute SHA-256 hash
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));

        // Convert to hex string
        var hex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        // Return first 16 characters
        return hex[..16];
    }
}
