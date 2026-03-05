namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Interface for persisting and retrieving grid state (column visibility, order, sorting, etc.).
/// </summary>
public interface IGridStateStore
{
    /// <summary>
    /// Saves the grid state for a specific table.
    /// </summary>
    /// <param name="tableKey">Unique identifier for the table (e.g., "Trades").</param>
    /// <param name="state">The state to persist.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveStateAsync(string tableKey, GridState state);

    /// <summary>
    /// Loads the grid state for a specific table.
    /// </summary>
    /// <param name="tableKey">Unique identifier for the table.</param>
    /// <returns>The persisted state, or null if not found.</returns>
    Task<GridState?> LoadStateAsync(string tableKey);

    /// <summary>
    /// Deletes the grid state for a specific table.
    /// </summary>
    /// <param name="tableKey">Unique identifier for the table.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteStateAsync(string tableKey);
}

/// <summary>
/// Represents persisted grid state: column visibility, ordering, widths, sorting, filtering, etc.
/// </summary>
public class GridState
{
    /// <summary>
    /// Gets or sets the column order (list of property names in desired order).
    /// </summary>
    public List<string> ColumnOrder { get; set; } = new();

    /// <summary>
    /// Gets or sets the hidden columns (list of property names that are hidden).
    /// </summary>
    public HashSet<string> HiddenColumns { get; set; } = new();

    /// <summary>
    /// Gets or sets column widths (property name => width string, e.g., "200px").
    /// </summary>
    public Dictionary<string, string> ColumnWidths { get; set; } = new();

    /// <summary>
    /// Gets or sets the current sort order.
    /// </summary>
    public List<SortDef> Sorts { get; set; } = new();

    /// <summary>
    /// Gets or sets the current filters.
    /// </summary>
    public List<FilterDef> Filters { get; set; } = new();

    /// <summary>
    /// Gets or sets the current grouping.
    /// </summary>
    public List<GroupDef> Groups { get; set; } = new();

    /// <summary>
    /// Gets or sets the current page index.
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the page size.
    /// </summary>
    public int PageSize { get; set; } = 25;

    /// <summary>
    /// Gets or sets the global search filter text.
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>
    /// Gets or sets the selected row keys (for row selection persistence).
    /// </summary>
    public HashSet<object> SelectedKeys { get; set; } = new();
}
