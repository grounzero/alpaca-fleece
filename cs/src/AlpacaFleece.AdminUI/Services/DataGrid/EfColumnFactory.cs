using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Auto-generates DbColumnDef instances from EF Core entity model metadata.
/// </summary>
public static class EfColumnFactory
{
    /// <summary>
    /// Generate column definitions from EF Core model for the given entity type.
    /// PK column is pinned (sticky), properties are ordered with PK first.
    /// </summary>
    public static IReadOnlyList<DbColumnDef<TItem>> Generate<TItem>(IModel model)
        where TItem : class
    {
        var entityType = model.FindEntityType(typeof(TItem))
            ?? throw new InvalidOperationException($"Entity type {typeof(TItem).Name} not found in model");

        var cols = new List<DbColumnDef<TItem>>();
        var pkProp = entityType.FindPrimaryKey()?.Properties.FirstOrDefault();

        // Add PK first if present
        if (pkProp is not null)
        {
            cols.Add(CreateColumnDef<TItem>(pkProp, true));
        }

        // Add remaining properties
        foreach (var prop in entityType.GetProperties())
        {
            if (prop != pkProp)
            {
                cols.Add(CreateColumnDef<TItem>(prop, false));
            }
        }

        return cols.AsReadOnly();
    }

    private static DbColumnDef<TItem> CreateColumnDef<TItem>(IProperty property, bool isKey)
        where TItem : class
    {
        var clrType = property.ClrType;
        var clrPropertyInfo = typeof(TItem).GetProperty(property.Name)
            ?? throw new InvalidOperationException($"Property {property.Name} not found on {typeof(TItem).Name}");

        // Build expression Func<TItem, object?> for value extraction
        var valueFunc = CompileValueFunc<TItem>(clrPropertyInfo);

        // Determine humanised title
        var title = Humanize(property.Name);

        // Detect type flags
        var isDateTimeOffset = clrType == typeof(DateTimeOffset) || clrType == typeof(DateTimeOffset?);
        var isNullableDateTimeOffset = clrType == typeof(DateTimeOffset?);
        var isBool = clrType == typeof(bool);
        var isDecimal = clrType == typeof(decimal) || clrType == typeof(decimal?);

        // Determine format string based on type and EF metadata
        var formatString = GetFormatString(clrType, property);

        // Determine if groupable (string columns only)
        var groupable = clrType == typeof(string);

        var col = new DbColumnDef<TItem>(
            propertyName: property.Name,
            title: title,
            propertyType: clrType,
            valueFunc: valueFunc)
        {
            FormatString = formatString,
            Sticky = isKey,
            IsDateTimeOffset = isDateTimeOffset || isNullableDateTimeOffset,
            IsBool = isBool,
            IsDecimal = isDecimal,
            Groupable = groupable,
            Hideable = !isKey, // PK is always visible
            Sortable = true,
            Filterable = true
        };

        return col;
    }

    /// <summary>
    /// Compile a Func<TItem, object?> for the given property.
    /// </summary>
    private static Func<TItem, object?> CompileValueFunc<TItem>(
        System.Reflection.PropertyInfo propertyInfo) where TItem : class
    {
        var param = Expression.Parameter(typeof(TItem), "item");
        var propAccess = Expression.Property(param, propertyInfo);

        // Box value types
        var boxed = propertyInfo.PropertyType.IsValueType
            ? Expression.Convert(propAccess, typeof(object))
            : (Expression)propAccess;

        var lambda = Expression.Lambda<Func<TItem, object?>>(boxed, param);
        return lambda.Compile();
    }

    /// <summary>
    /// Humanise PascalCase property name to title case (e.g. "RealizedPnl" → "Realized Pnl").
    /// </summary>
    private static string Humanize(string propertyName)
    {
        // Insert space before uppercase letters (except the first)
        var spaced = Regex.Replace(propertyName, @"([A-Z])", " $1").Trim();
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    /// <summary>
    /// Determine format string for a property based on its type and EF precision.
    /// </summary>
    private static string? GetFormatString(Type clrType, IProperty efProperty)
    {
        // DateTimeOffset / DateTimeOffset?
        if (clrType == typeof(DateTimeOffset) || clrType == typeof(DateTimeOffset?))
            return "yyyy-MM-dd HH:mm:ss";

        // DateTime
        if (clrType == typeof(DateTime))
            return "yyyy-MM-dd HH:mm";

        // Decimal: use precision to determine decimal places
        if (clrType == typeof(decimal) || clrType == typeof(decimal?))
        {
            var precision = efProperty.GetPrecision();
            if (precision.HasValue)
            {
                var scale = efProperty.GetScale() ?? 0;
                // Crypto (e.g. Precision(18,8)): use general format
                if (scale >= 8)
                    return "G";
                // Standard financial (e.g. Precision(10,4)): show 4 decimals
                if (scale == 4)
                    return "N4";
                // Default: 2 decimals
                return "N2";
            }
            // Fallback: 2 decimals
            return "N2";
        }

        return null;
    }
}
