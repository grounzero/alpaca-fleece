namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Defines grouping for a grid column.
/// </summary>
public class GroupDef
{
    /// <summary>
    /// Gets or sets the property name to group by.
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the grouping order (0-based).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupDef"/> class.
    /// </summary>
    public GroupDef()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupDef"/> class.
    /// </summary>
    public GroupDef(string propertyName, int order = 0)
    {
        PropertyName = propertyName;
        Order = order;
    }
}
