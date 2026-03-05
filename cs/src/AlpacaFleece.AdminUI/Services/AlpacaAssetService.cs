using System.Text.Json;

namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Fetches tradable asset lists from the Alpaca Markets API.
/// Uses the API credentials stored in the bot's appsettings.json.
/// Results are cached for one hour to respect rate limits.
/// </summary>
public sealed class AlpacaAssetService(
    ConfigService configService,
    IHttpClientFactory httpClientFactory,
    ILogger<AlpacaAssetService> logger)
{
    private const string PaperBaseUrl = "https://paper-api.alpaca.markets";
    private List<AssetInfo>? _equityCache;
    private List<AssetInfo>? _cryptoCache;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async ValueTask<IReadOnlyList<AssetInfo>> GetEquityAssetsAsync(CancellationToken ct = default)
    {
        await EnsureCacheAsync(ct);
        return _equityCache ?? [];
    }

    public async ValueTask<IReadOnlyList<AssetInfo>> GetCryptoAssetsAsync(CancellationToken ct = default)
    {
        await EnsureCacheAsync(ct);
        return _cryptoCache ?? [];
    }

    private async ValueTask EnsureCacheAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _cacheExpiry) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _cacheExpiry) return;

            var draft = await configService.ReadDraftAsync(ct);
            if (string.IsNullOrWhiteSpace(draft.ApiKey))
            {
                _equityCache = [];
                _cryptoCache = [];
                return;
            }

            _equityCache = await FetchAssetsAsync("us_equity", draft.ApiKey, draft.SecretKey, ct);
            _cryptoCache = await FetchAssetsAsync("crypto", draft.ApiKey, draft.SecretKey, ct);
            _cacheExpiry = DateTimeOffset.UtcNow.AddHours(1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Alpaca assets; returning empty lists");
            _equityCache = [];
            _cryptoCache = [];
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<AssetInfo>> FetchAssetsAsync(
        string assetClass, string apiKey, string secretKey, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", apiKey);
        client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secretKey);

        var url = $"{PaperBaseUrl}/v2/assets?status=active&asset_class={assetClass}";
        var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Alpaca assets API returned {Status} for {AssetClass}", resp.StatusCode, assetClass);
            return [];
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var result = new List<AssetInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var tradable = item.TryGetProperty("tradable", out var t) && t.GetBoolean();
            if (!tradable) continue;

            result.Add(new AssetInfo(
                Symbol: item.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "",
                Name: item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Exchange: item.TryGetProperty("exchange", out var e) ? e.GetString() ?? "" : "",
                AssetClass: assetClass,
                Tradable: tradable));
        }

        return result;
    }
}
