using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;

namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Defines metadata for a grid column.
/// </summary>
/// <typeparam name="TItem">The type of the data item.</typeparam>
public class DbColumnDef<TItem>
{
    /// <summary>
    /// Gets or sets the property name (path for nested properties, e.g., "Address.City").
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display title for the column header.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the property type (e.g., typeof(string), typeof(decimal)).
    /// </summary>
    public Type PropertyType { get; set; } = typeof(object);

    /// <summary>
    /// Gets or sets a value indicating whether the property is nullable.
    /// </summary>
    public bool IsNullable { get; set; }

    /// <summary>
    /// Gets or sets the maximum length for string properties.
    /// </summary>
    public int? MaxLength { get; set; }

    /// <summary>
    /// Gets or sets the format string for display (e.g., "N2", "yyyy-MM-dd").
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a key column.
    /// </summary>
    public bool IsKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this column is hidden by default.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this column can be resized.
    /// </summary>
    public bool Resizable { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether this column is sortable.
    /// </summary>
    public bool Sortable { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether this column is filterable.
    /// </summary>
    public bool Filterable { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether this column is pinned (always visible).
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// Gets or sets the initial column width.
    /// </summary>
    public string? Width { get; set; } = "200px";

    /// <summary>
    /// Gets or sets a value indicating whether this column can be hidden.
    /// </summary>
    public bool Hideable { get; set; } = true;

    /// <summary>
    /// Gets or sets the computed property getter for display (if null, uses reflection).
    /// </summary>
    public Func<TItem, object?>? GetValue { get; set; }

    /// <summary>
    /// Gets or sets the template for rendering the column (PropertyColumn uses this).
    /// </summary>
    public RenderFragment<TItem>? ColumnTemplate { get; set; }

    /// <summary>
    /// Gets or sets the template for the filter UI (optional, default based on type).
    /// </summary>
    public RenderFragment<FilterDef>? FilterTemplate { get; set; }

    /// <summary>
    /// Gets or sets the template for the edit UI (optional, default based on type).
    /// </summary>
    public RenderFragment<TItem>? EditTemplate { get; set; }

    /// <summary>
    /// Gets or sets the alignment (Left, Center, Right, Justify).
    /// </summary>
    public string Alignment { get; set; } = "Left";

    /// <summary>
    /// Gets or sets available responsive breakpoints where this column should be visible.
    /// E.g., new() { "sm", "md", "lg" } means visible on small and up, hidden on xs.
    /// </summary>
    public HashSet<string> ResponsiveBreakpoints { get; set; } = new() { "xs", "sm", "md", "lg", "xl" };

    /// <summary>
    /// Initializes a new instance of the <see cref="DbColumnDef{TItem}"/> class.
    /// </summary>
    public DbColumnDef()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbColumnDef{TItem}"/> class.
    /// </summary>
    public DbColumnDef(string propertyName, string title, Type propertyType)
    {
        PropertyName = propertyName;
        Title = title;
        PropertyType = propertyType;
    }
}
