namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Enumeration of supported filter operators for grid columns.
/// </summary>
public enum FilterOperator
{
    /// <summary>
    /// Equals comparison.
    /// </summary>
    Equals,

    /// <summary>
    /// Not equals comparison.
    /// </summary>
    NotEquals,

    /// <summary>
    /// String contains.
    /// </summary>
    Contains,

    /// <summary>
    /// String starts with.
    /// </summary>
    StartsWith,

    /// <summary>
    /// String ends with.
    /// </summary>
    EndsWith,

    /// <summary>
    /// Greater than.
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Greater than or equals.
    /// </summary>
    GreaterThanOrEquals,

    /// <summary>
    /// Less than.
    /// </summary>
    LessThan,

    /// <summary>
    /// Less than or equals.
    /// </summary>
    LessThanOrEquals,

    /// <summary>
    /// Value is between two values.
    /// </summary>
    Between,

    /// <summary>
    /// Value is null.
    /// </summary>
    IsNull,

    /// <summary>
    /// Value is not null.
    /// </summary>
    IsNotNull,

    /// <summary>
    /// Value is in a list.
    /// </summary>
    In
}
