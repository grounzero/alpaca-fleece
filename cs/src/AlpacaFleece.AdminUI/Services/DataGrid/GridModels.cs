namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Filter operators for server-side filtering.
/// </summary>
public enum FilterOperator
{
    Contains,
    NotContains,
    Equals,
    NotEquals,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    IsNull,
    IsNotNull,
    Between,
    In
}

/// <summary>
/// Represents a column filter definition.
/// </summary>
public sealed record FilterDef(
    string Property,
    FilterOperator Operator,
    string? Value,
    string? Value2 = null);

/// <summary>
/// Represents a column sort definition.
/// </summary>
public sealed record SortDef(string Property, bool Descending);

/// <summary>
/// Persisted state for a grid table (page size, hidden columns, default sort).
/// </summary>
public sealed record GridPersistedState(
    int PageSize,
    string[] HiddenColumns,
    string? DefaultSortProperty = null,
    bool DefaultSortDescending = true);

/// <summary>
/// Column definition for DbTableGrid. Defines metadata and rendering for a single grid column.
/// </summary>
public sealed class DbColumnDef<TItem> where TItem : class
{
    /// <summary>
    /// The EF Core property name (e.g. "Symbol").
    /// </summary>
    public string PropertyName { get; init; }

    /// <summary>
    /// Display title for the column header (e.g. "Symbol").
    /// </summary>
    public string Title { get; init; }

    /// <summary>
    /// The CLR type of this property (e.g. typeof(string), typeof(decimal)).
    /// </summary>
    public Type PropertyType { get; init; }

    /// <summary>
    /// Compiled function to extract the property value from an item.
    /// </summary>
    public Func<TItem, object?> ValueFunc { get; init; }

    /// <summary>
    /// Format string for value display (e.g. "N2", "yyyy-MM-dd HH:mm").
    /// </summary>
    public string? FormatString { get; init; }

    /// <summary>
    /// Whether this column can be hidden by the user.
    /// </summary>
    public bool Hideable { get; init; } = true;

    /// <summary>
    /// Whether this column can be sorted.
    /// </summary>
    public bool Sortable { get; init; } = true;

    /// <summary>
    /// Whether this column can be filtered.
    /// </summary>
    public bool Filterable { get; init; } = true;

    /// <summary>
    /// Whether this column can be grouped.
    /// </summary>
    public bool Groupable { get; init; } = false;

    /// <summary>
    /// Whether this column is sticky (pinned left).
    /// </summary>
    public bool Sticky { get; init; } = false;

    /// <summary>
    /// Whether the property type is DateTimeOffset (affects sorting in SQLite).
    /// </summary>
    public bool IsDateTimeOffset { get; init; } = false;

    /// <summary>
    /// Whether the property type is decimal.
    /// </summary>
    public bool IsDecimal { get; init; } = false;

    /// <summary>
    /// Whether the property type is bool.
    /// </summary>
    public bool IsBool { get; init; } = false;

    /// <summary>
    /// Optional CSS class for the column.
    /// </summary>
    public string? CssClass { get; init; }

    /// <summary>
    /// Constructor requiring all essential fields.
    /// </summary>
    public DbColumnDef(
        string propertyName,
        string title,
        Type propertyType,
        Func<TItem, object?> valueFunc)
    {
        PropertyName = propertyName;
        Title = title;
        PropertyType = propertyType;
        ValueFunc = valueFunc;
    }
}
