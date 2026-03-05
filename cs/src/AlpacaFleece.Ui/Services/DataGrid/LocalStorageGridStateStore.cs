using System.Text.Json;
using Blazored.LocalStorage;

namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// LocalStorage-based implementation of <see cref="IGridStateStore"/> using Blazored.LocalStorage.
/// </summary>
public class LocalStorageGridStateStore : IGridStateStore
{
    private const string StateKeyPrefix = "grid_state_";
    private readonly ILocalStorageService _localStorage;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalStorageGridStateStore"/> class.
    /// </summary>
    /// <param name="localStorage">The Blazored LocalStorage service.</param>
    public LocalStorageGridStateStore(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Saves the grid state for a specific table.
    /// </summary>
    public async Task SaveStateAsync(string tableKey, GridState state)
    {
        var key = StateKeyPrefix + tableKey;
        try
        {
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await _localStorage.SetItemAsStringAsync(key, json);
        }
        catch (Exception ex)
        {
            // Log or handle LocalStorage errors gracefully
            System.Diagnostics.Debug.WriteLine($"Failed to save grid state for {tableKey}: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the grid state for a specific table.
    /// </summary>
    public async Task<GridState?> LoadStateAsync(string tableKey)
    {
        var key = StateKeyPrefix + tableKey;
        try
        {
            var json = await _localStorage.GetItemAsStringAsync(key);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            return JsonSerializer.Deserialize<GridState>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            // Log or handle errors gracefully
            System.Diagnostics.Debug.WriteLine($"Failed to load grid state for {tableKey}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Deletes the grid state for a specific table.
    /// </summary>
    public async Task DeleteStateAsync(string tableKey)
    {
        var key = StateKeyPrefix + tableKey;
        try
        {
            await _localStorage.RemoveItemAsync(key);
        }
        catch (Exception ex)
        {
            // Log or handle errors gracefully
            System.Diagnostics.Debug.WriteLine($"Failed to delete grid state for {tableKey}: {ex.Message}");
        }
    }
}
