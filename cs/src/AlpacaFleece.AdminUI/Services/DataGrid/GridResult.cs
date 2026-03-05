namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Represents the result of a grid data query.
/// </summary>
/// <typeparam name="TItem">The type of items in the result.</typeparam>
public class GridResult<TItem>
{
    /// <summary>
    /// Gets or sets the list of items for the current page.
    /// </summary>
    public IReadOnlyList<TItem> Items { get; set; } = new List<TItem>();

    /// <summary>
    /// Gets or sets the total number of items (across all pages) matching the query filters.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GridResult{TItem}"/> class.
    /// </summary>
    public GridResult()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GridResult{TItem}"/> class.
    /// </summary>
    public GridResult(IReadOnlyList<TItem> items, int totalItems)
    {
        Items = items;
        TotalItems = totalItems;
    }
}
