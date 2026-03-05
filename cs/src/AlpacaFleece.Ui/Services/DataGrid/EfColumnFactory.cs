using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Generates column definitions from EF Core entity metadata.
/// </summary>
public class EfColumnFactory
{
    private readonly DbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="EfColumnFactory"/> class.
    /// </summary>
    /// <param name="context">The DbContext.</param>
    public EfColumnFactory(DbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Generates column definitions for an entity type by inspecting EF Core metadata.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>A list of generated column definitions.</returns>
    public List<DbColumnDef<TEntity>> GenerateColumns<TEntity>() where TEntity : class
    {
        var result = new List<DbColumnDef<TEntity>>();
        var entityType = _context.Model.FindEntityType(typeof(TEntity));

        if (entityType == null)
        {
            return result;
        }

        // Get all properties from metadata
        foreach (var property in entityType.GetProperties())
        {
            var clrProperty = property.PropertyInfo;
            if (clrProperty == null)
            {
                continue;
            }

            var columnDef = new DbColumnDef<TEntity>
            {
                PropertyName = property.Name,
                Title = HumanizePropertyName(property.Name),
                PropertyType = property.ClrType,
                IsNullable = property.IsNullable,
                IsKey = property.IsPrimaryKey(),
                Format = GetDefaultFormat(property.ClrType),
                Pinned = property.IsPrimaryKey(),
                Alignment = GetAlignment(property.ClrType)
            };

            // Set max length for strings
            if (property.ClrType == typeof(string) || property.ClrType == typeof(string?))
            {
                var maxLength = property.GetMaxLength();
                if (maxLength.HasValue)
                {
                    columnDef.MaxLength = maxLength.Value;
                }
            }

            result.Add(columnDef);
        }

        return result;
    }

    /// <summary>
    /// Humanizes a property name (e.g., "ClientOrderId" => "Client Order Id").
    /// </summary>
    private string HumanizePropertyName(string propertyName)
    {
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < propertyName.Length; i++)
        {
            var ch = propertyName[i];
            if (i > 0 && char.IsUpper(ch))
            {
                result.Append(' ');
            }
            result.Append(ch);
        }
        return result.ToString();
    }

    /// <summary>
    /// Gets the default format string for a property type.
    /// </summary>
    private string? GetDefaultFormat(Type propertyType)
    {
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        return underlyingType switch
        {
            Type t when t == typeof(DateTime) => "yyyy-MM-dd HH:mm:ss",
            Type t when t == typeof(DateOnly) => "yyyy-MM-dd",
            Type t when t == typeof(TimeOnly) => "HH:mm:ss",
            Type t when t == typeof(decimal) || t == typeof(float) || t == typeof(double) => "N2",
            Type t when t == typeof(int) || t == typeof(long) || t == typeof(short) => "D",
            Type t when t == typeof(byte) => "D",
            _ => null
        };
    }

    /// <summary>
    /// Gets the alignment for a property type.
    /// </summary>
    private string GetAlignment(Type propertyType)
    {
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (IsNumericType(underlyingType) || underlyingType == typeof(DateTime) || underlyingType == typeof(DateOnly))
        {
            return "Right";
        }

        return "Left";
    }

    /// <summary>
    /// Determines if a type is numeric.
    /// </summary>
    private bool IsNumericType(Type type)
    {
        return type == typeof(int) || type == typeof(long) || type == typeof(short) ||
               type == typeof(decimal) || type == typeof(float) || type == typeof(double) ||
               type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) ||
               type == typeof(ushort) || type == typeof(sbyte);
    }
}
