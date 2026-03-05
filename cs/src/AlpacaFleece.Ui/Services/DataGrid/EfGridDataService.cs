using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace AlpacaFleece.Ui.Services.DataGrid;

/// <summary>
/// Service for translating grid queries into EF Core LINQ operations.
/// Supports filtering, sorting, paging, and searching using expression trees (no string concatenation).
/// </summary>
public class EfGridDataService
{
    /// <summary>
    /// Executes a grid query against an IQueryable source, applying filters, sorting, and paging.
    /// </summary>
    /// <typeparam name="TItem">The type of items in the query.</typeparam>
    /// <param name="source">The IQueryable source (typically DbSet).</param>
    /// <param name="query">The grid query parameters.</param>
    /// <param name="searchableProperties">Optional list of property names to include in global search. If null, all string properties are searched.</param>
    /// <returns>A task returning the grid result with paginated items and total count.</returns>
    public async Task<GridResult<TItem>> QueryAsync<TItem>(
        IQueryable<TItem> source,
        GridQuery query,
        IEnumerable<string>? searchableProperties = null) where TItem : class
    {
        // Apply global search filter first
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            source = ApplyGlobalSearch(source, query.SearchText, searchableProperties);
        }

        // Apply column filters
        foreach (var filter in query.Filters)
        {
            source = ApplyFilter(source, filter);
        }

        // Get the total count before paging
        var totalItems = await source.CountAsync();

        // Apply sorting
        source = ApplySort(source, query.Sorts);

        // Apply paging
        source = source.Skip(query.PageIndex * query.PageSize).Take(query.PageSize);

        // Execute the query and return the result
        var items = await source.ToListAsync();
        return new GridResult<TItem>(items, totalItems);
    }

    /// <summary>
    /// Applies a global search filter across multiple string properties.
    /// </summary>
    private IQueryable<TItem> ApplyGlobalSearch<TItem>(
        IQueryable<TItem> source,
        string searchText,
        IEnumerable<string>? propertiesToSearch) where TItem : class
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return source;
        }

        var type = typeof(TItem);
        var parameter = Expression.Parameter(type, "x");
        var searchLower = searchText.ToLower();

        // Determine which properties to search
        var properties = propertiesToSearch != null
            ? GetPropertiesByNames(type, propertiesToSearch)
            : GetStringProperties(type);

        if (properties.Count == 0)
        {
            return source;
        }

        // Build filter: x => x.Property1.ToLower().Contains(searchLower) || x.Property2.ToLower().Contains(searchLower) || ...
        Expression? predicateExpression = null;
        foreach (var property in properties)
        {
            var propertyAccess = Expression.Property(parameter, property);
            var toLower = Expression.Call(propertyAccess, "ToLower", null);
            var contains = Expression.Call(
                toLower,
                "Contains",
                null,
                Expression.Constant(searchLower));

            predicateExpression = predicateExpression == null
                ? contains
                : Expression.OrElse(predicateExpression, contains);
        }

        if (predicateExpression == null)
        {
            return source;
        }

        var lambda = Expression.Lambda<Func<TItem, bool>>(predicateExpression, parameter);
        return source.Where(lambda);
    }

    /// <summary>
    /// Applies a single column filter using the specified operator.
    /// </summary>
    private IQueryable<TItem> ApplyFilter<TItem>(IQueryable<TItem> source, FilterDef filter) where TItem : class
    {
        var type = typeof(TItem);
        var parameter = Expression.Parameter(type, "x");

        // Navigate nested properties (e.g., "Address.City")
        Expression propertyExpression = GetPropertyExpression(parameter, filter.PropertyName);
        if (propertyExpression == null)
        {
            return source;
        }

        Expression? predicate = filter.Operator switch
        {
            FilterOperator.Equals => BuildEqualsExpression(propertyExpression, filter.Value),
            FilterOperator.NotEquals => Expression.Not(BuildEqualsExpression(propertyExpression, filter.Value)),
            FilterOperator.Contains => BuildContainsExpression(propertyExpression, filter.Value),
            FilterOperator.StartsWith => BuildStartsWithExpression(propertyExpression, filter.Value),
            FilterOperator.EndsWith => BuildEndsWithExpression(propertyExpression, filter.Value),
            FilterOperator.GreaterThan => BuildComparisonExpression(propertyExpression, filter.Value, ExpressionType.GreaterThan),
            FilterOperator.GreaterThanOrEquals => BuildComparisonExpression(propertyExpression, filter.Value, ExpressionType.GreaterThanOrEqual),
            FilterOperator.LessThan => BuildComparisonExpression(propertyExpression, filter.Value, ExpressionType.LessThan),
            FilterOperator.LessThanOrEquals => BuildComparisonExpression(propertyExpression, filter.Value, ExpressionType.LessThanOrEqual),
            FilterOperator.Between => BuildBetweenExpression(propertyExpression, filter.Value, filter.Value2),
            FilterOperator.IsNull => Expression.Equal(propertyExpression, Expression.Constant(null)),
            FilterOperator.IsNotNull => Expression.NotEqual(propertyExpression, Expression.Constant(null)),
            FilterOperator.In => BuildInExpression(propertyExpression, filter.Value),
            _ => null
        };

        if (predicate == null)
        {
            return source;
        }

        var lambda = Expression.Lambda<Func<TItem, bool>>(predicate, parameter);
        return source.Where(lambda);
    }

    /// <summary>
    /// Applies sort orders from the query.
    /// </summary>
    private IQueryable<TItem> ApplySort<TItem>(IQueryable<TItem> source, List<SortDef> sorts) where TItem : class
    {
        if (sorts.Count == 0)
        {
            return source;
        }

        var type = typeof(TItem);
        var parameter = Expression.Parameter(type, "x");
        IQueryable<TItem> sortedSource = source;

        for (int i = 0; i < sorts.Count; i++)
        {
            var sort = sorts[i];
            var propertyExpression = GetPropertyExpression(parameter, sort.PropertyName);
            if (propertyExpression == null)
            {
                continue;
            }

            var lambda = Expression.Lambda(propertyExpression, parameter);

            // Use OrderBy for first sort, ThenBy for subsequent sorts
            var methodName = i == 0
                ? (sort.Descending ? "OrderByDescending" : "OrderBy")
                : (sort.Descending ? "ThenByDescending" : "ThenBy");

            var resultType = propertyExpression.Type;
            var method = typeof(Queryable)
                .GetMethods()
                .First(m => m.Name == methodName &&
                    m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(type, resultType);

            sortedSource = (IQueryable<TItem>)method.Invoke(null, new object[] { sortedSource, lambda })!;
        }

        return sortedSource;
    }

    /// <summary>
    /// Gets a property expression, handling nested properties.
    /// </summary>
    private Expression? GetPropertyExpression(ParameterExpression parameter, string propertyPath)
    {
        var parts = propertyPath.Split('.');
        Expression expr = parameter;

        foreach (var part in parts)
        {
            var property = expr.Type.GetProperty(part, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                return null;
            }
            expr = Expression.Property(expr, property);
        }

        return expr;
    }

    /// <summary>
    /// Builds an equals comparison expression.
    /// </summary>
    private Expression BuildEqualsExpression(Expression propertyExpression, object? value)
    {
        return Expression.Equal(propertyExpression, Expression.Constant(Convert.ChangeType(value, propertyExpression.Type)));
    }

    /// <summary>
    /// Builds a comparison expression (>, >=, <, <=).
    /// </summary>
    private Expression BuildComparisonExpression(Expression propertyExpression, object? value, ExpressionType comparisonType)
    {
        try
        {
            var convertedValue = Convert.ChangeType(value, propertyExpression.Type);
            return Expression.MakeBinary(comparisonType, propertyExpression, Expression.Constant(convertedValue));
        }
        catch
        {
            return Expression.Constant(true);
        }
    }

    /// <summary>
    /// Builds a string Contains expression.
    /// </summary>
    private Expression BuildContainsExpression(Expression propertyExpression, object? value)
    {
        if (propertyExpression.Type != typeof(string))
        {
            return Expression.Constant(true);
        }

        var valueLower = Expression.Call(Expression.Constant(value?.ToString() ?? ""), "ToLower", null);
        var toLower = Expression.Call(propertyExpression, "ToLower", null);
        return Expression.Call(toLower, "Contains", null, valueLower);
    }

    /// <summary>
    /// Builds a string StartsWith expression.
    /// </summary>
    private Expression BuildStartsWithExpression(Expression propertyExpression, object? value)
    {
        if (propertyExpression.Type != typeof(string))
        {
            return Expression.Constant(true);
        }

        var valueLower = Expression.Call(Expression.Constant(value?.ToString() ?? ""), "ToLower", null);
        var toLower = Expression.Call(propertyExpression, "ToLower", null);
        return Expression.Call(toLower, "StartsWith", null, valueLower);
    }

    /// <summary>
    /// Builds a string EndsWith expression.
    /// </summary>
    private Expression BuildEndsWithExpression(Expression propertyExpression, object? value)
    {
        if (propertyExpression.Type != typeof(string))
        {
            return Expression.Constant(true);
        }

        var valueLower = Expression.Call(Expression.Constant(value?.ToString() ?? ""), "ToLower", null);
        var toLower = Expression.Call(propertyExpression, "ToLower", null);
        return Expression.Call(toLower, "EndsWith", null, valueLower);
    }

    /// <summary>
    /// Builds a Between expression for range filters.
    /// </summary>
    private Expression BuildBetweenExpression(Expression propertyExpression, object? value1, object? value2)
    {
        try
        {
            var lowerValue = Convert.ChangeType(value1, propertyExpression.Type);
            var upperValue = Convert.ChangeType(value2, propertyExpression.Type);

            var gteExpr = Expression.GreaterThanOrEqual(propertyExpression, Expression.Constant(lowerValue));
            var lteExpr = Expression.LessThanOrEqual(propertyExpression, Expression.Constant(upperValue));
            return Expression.AndAlso(gteExpr, lteExpr);
        }
        catch
        {
            return Expression.Constant(true);
        }
    }

    /// <summary>
    /// Builds an In expression for list filters.
    /// </summary>
    private Expression BuildInExpression(Expression propertyExpression, object? value)
    {
        if (value is not System.Collections.IEnumerable enumerable)
        {
            return Expression.Constant(false);
        }

        var list = new List<object>();
        foreach (var item in enumerable)
        {
            if (item != null)
            {
                list.Add(item);
            }
        }

        if (list.Count == 0)
        {
            return Expression.Constant(false);
        }

        // Create: x => list.Contains(x.Property)
        var listExpr = Expression.Constant(list);
        var containsMethod = typeof(List<object>).GetMethod("Contains", new[] { typeof(object) });
        if (containsMethod != null)
        {
            var boxedProperty = Expression.Convert(propertyExpression, typeof(object));
            return Expression.Call(listExpr, containsMethod, boxedProperty);
        }

        return Expression.Constant(true);
    }

    /// <summary>
    /// Gets all string properties of a type.
    /// </summary>
    private List<PropertyInfo> GetStringProperties(Type type)
    {
        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead)
            .ToList();
    }

    /// <summary>
    /// Gets properties by name.
    /// </summary>
    private List<PropertyInfo> GetPropertiesByNames(Type type, IEnumerable<string> names)
    {
        var result = new List<PropertyInfo>();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                result.Add(prop);
            }
        }
        return result;
    }
}
