namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Defines a sort order for a grid column.
/// </summary>
public class SortDef
{
    /// <summary>
    /// Gets or sets the property name to sort by.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the sort is descending.
    /// </summary>
    public bool Descending { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SortDef"/> class.
    /// </summary>
    public SortDef()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SortDef"/> class.
    /// </summary>
    public SortDef(string propertyName, bool descending = false)
    {
        PropertyName = propertyName;
        Descending = descending;
    }
}
