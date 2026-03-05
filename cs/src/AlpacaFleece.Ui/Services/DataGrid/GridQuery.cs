namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Represents a grid query with paging, sorting, filtering, and grouping parameters.
/// </summary>
public class GridQuery
{
    /// <summary>
    /// Gets or sets the current page number (0-based).
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the page size (number of records per page).
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// Gets or sets the global search filter text.
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>
    /// Gets or sets the list of column sort orders.
    /// </summary>
    public List<SortDef> Sorts { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of column filters.
    /// </summary>
    public List<FilterDef> Filters { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of grouping definitions.
    /// </summary>
    public List<GroupDef> Groups { get; set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GridQuery"/> class.
    /// </summary>
    public GridQuery()
    {
        PageIndex = 0;
        PageSize = 25;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GridQuery"/> class with paging options.
    /// </summary>
    public GridQuery(int pageIndex, int pageSize)
    {
        PageIndex = pageIndex;
        PageSize = pageSize;
    }
}
