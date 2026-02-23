namespace AlpacaFleece.Infrastructure.Broker;

/// <summary>
/// Configuration for Alpaca broker connection.
/// </summary>
public sealed class BrokerOptions
{
    /// <summary>
    /// Alpaca API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Alpaca secret key.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for Alpaca API (e.g., https://paper-api.alpaca.markets).
    /// </summary>
    public string BaseUrl { get; set; } = "https://paper-api.alpaca.markets";

    /// <summary>
    /// If true, use paper trading (default). If false, live trading.
    /// </summary>
    public bool IsPaperTrading { get; set; } = true;

    /// <summary>
    /// Dual gate: allow live trading only if both Paper is false AND this is true.
    /// </summary>
    public bool AllowLiveTrading { get; set; } = false;

    /// <summary>
    /// If true, don't actually submit orders to Alpaca (dry run mode).
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// If true, immediately reject all signals (kill switch).
    /// </summary>
    public bool KillSwitch { get; set; } = false;

    /// <summary>
    /// Request timeout in milliseconds.
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Validates configuration.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("BrokerOptions.ApiKey is required");
        if (string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidOperationException("BrokerOptions.SecretKey is required");
        if (!IsPaperTrading && !AllowLiveTrading)
            throw new InvalidOperationException(
                "Live trading requires IsPaperTrading=false AND AllowLiveTrading=true");
    }
}
