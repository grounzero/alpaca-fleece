namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Defines a filter condition for a grid column.
/// </summary>
public class FilterDef
{
    /// <summary>
    /// Gets or sets the property name to filter by.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filter operator.
    /// </summary>
    public FilterOperator Operator { get; set; } = FilterOperator.Equals;

    /// <summary>
    /// Gets or sets the filter value.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Gets or sets the secondary value for range filters (e.g., Between operator).
    /// </summary>
    public object? Value2 { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterDef"/> class.
    /// </summary>
    public FilterDef()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FilterDef"/> class.
    /// </summary>
    public FilterDef(string propertyName, FilterOperator op, object? value, object? value2 = null)
    {
        PropertyName = propertyName;
        Operator = op;
        Value = value;
        Value2 = value2;
    }
}
