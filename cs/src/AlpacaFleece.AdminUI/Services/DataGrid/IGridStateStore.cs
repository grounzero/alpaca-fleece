namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Stores and retrieves persisted grid state (column visibility, page size, etc.).
/// </summary>
public interface IGridStateStore
{
    /// <summary>
    /// Load persisted state for a grid table, identified by tableKey (e.g. "Trades").
    /// Returns null if no state has been persisted yet.
    /// </summary>
    Task<GridPersistedState?> LoadAsync(string tableKey, CancellationToken ct = default);

    /// <summary>
    /// Persist state for a grid table.
    /// </summary>
    Task SaveAsync(string tableKey, GridPersistedState state, CancellationToken ct = default);

    /// <summary>
    /// Clear persisted state for a grid table.
    /// </summary>
    Task ClearAsync(string tableKey, CancellationToken ct = default);
}
