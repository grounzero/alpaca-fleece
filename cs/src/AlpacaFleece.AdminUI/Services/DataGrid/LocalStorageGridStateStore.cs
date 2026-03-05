using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Grid state store backed by browser localStorage via LocalStorageService.
/// </summary>
public sealed class LocalStorageGridStateStore(
    LocalStorageService localStorage,
    ILogger<LocalStorageGridStateStore> logger) : IGridStateStore
{
    private const string KeyPrefix = "grid-state:";

    public async Task<GridPersistedState?> LoadAsync(string tableKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableKey))
            return null;

        try
        {
            var key = $"{KeyPrefix}{tableKey}";
            var json = await localStorage.GetAsync(key, ct);
            if (string.IsNullOrEmpty(json))
                return null;

            var state = JsonSerializer.Deserialize<GridPersistedState>(json);
            return state;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load grid state for table {TableKey}", tableKey);
            return null;
        }
    }

    public async Task SaveAsync(string tableKey, GridPersistedState state, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableKey) || state is null)
            return;

        try
        {
            var key = $"{KeyPrefix}{tableKey}";
            var json = JsonSerializer.Serialize(state);
            await localStorage.SetAsync(key, json, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save grid state for table {TableKey}", tableKey);
        }
    }

    public async Task ClearAsync(string tableKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableKey))
            return;

        try
        {
            var key = $"{KeyPrefix}{tableKey}";
            await localStorage.RemoveAsync(key, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear grid state for table {TableKey}", tableKey);
        }
    }
}
