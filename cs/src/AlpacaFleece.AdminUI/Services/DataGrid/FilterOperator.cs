namespace AlpacaFleece.AdminUI.Services.DataGrid;

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
    /// Is null.
    /// </summary>
    IsNull,

    /// <summary>
    /// Is not null.
    /// </summary>
    IsNotNull,

    /// <summary>
    /// Between two values (uses Value and Value2).
    /// </summary>
    Between,

    /// <summary>
    /// In a list of values.
    /// </summary>
    In,

    /// <summary>
    /// Not in a list of values.
    /// </summary>
    NotIn
}
