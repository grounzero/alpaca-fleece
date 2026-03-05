using System.Reflection;

namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Adapts AdminDbService data to work with DbTableGrid by applying filtering, sorting, and searching
/// to dynamic object rows using reflection (since EF expression trees don't work with dynamic object types).
/// </summary>
public class AdminGridDataService
{
    private readonly AdminDbService _adminDbService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminGridDataService"/> class.
    /// </summary>
    public AdminGridDataService(AdminDbService adminDbService)
    {
        _adminDbService = adminDbService;
    }

    /// <summary>
    /// Queries a database table with filtering, sorting, searching, and paging.
    /// </summary>
    /// <param name="tableName">The name of the table to query (e.g., "OrderIntents", "Trades").</param>
    /// <param name="query">The grid query parameters.</param>
    /// <returns>Grid result with paginated rows and total count.</returns>
    public async Task<GridResult<object>> QueryAsync(string tableName, GridQuery query)
    {
        // Get all rows for the table using AdminDbService
        var (allRows, total, columns) = await _adminDbService.GetTableDataAsync(tableName, page: 1, pageSize: 10000);
        var columnsList = columns.ToList();

        if (allRows.Count == 0)
        {
            return new GridResult<object>(new List<object>(), 0);
        }

        // Apply global search filter
        var filtered = allRows.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            filtered = ApplyGlobalSearch(filtered, query.SearchText, columnsList);
        }

        // Apply column-specific filters
        foreach (var filter in query.Filters)
        {
            filtered = ApplyFilter(filtered, filter);
        }

        // Get total count after filtering
        var filteredList = filtered.ToList();
        var filteredTotal = filteredList.Count;

        // Apply sorting
        if (query.Sorts.Any())
        {
            filteredList = ApplySort(filteredList, query.Sorts);
        }

        // Apply paging
        var pagedItems = filteredList
            .Skip(query.PageIndex * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        return new GridResult<object>(pagedItems, filteredTotal);
    }

    /// <summary>
    /// Gets column definitions for a specific table.
    /// </summary>
    public async Task<List<DbColumnDef<object>>> GetColumnsAsync(string tableName)
    {
        var (_, _, columns) = await _adminDbService.GetTableDataAsync(tableName, page: 1, pageSize: 1);

        var columnDefs = new List<DbColumnDef<object>>();
        foreach (var col in columns)
        {
            columnDefs.Add(new DbColumnDef<object>
            {
                PropertyName = col,
                Title = HumanizePropertyName(col),
                PropertyType = typeof(string),
                IsNullable = true,
                Sortable = true,
                Filterable = true,
                Hideable = true,
                Resizable = true,
                Width = "200px"
            });
        }

        return columnDefs;
    }

    /// <summary>
    /// Applies global search across all visible columns.
    /// </summary>
    private IEnumerable<object> ApplyGlobalSearch(IEnumerable<object> rows, string searchText, List<string> columns)
    {
        var lowerSearch = searchText.ToLower();

        return rows.Where(row =>
        {
            foreach (var column in columns)
            {
                var value = GetPropertyValue(row, column);
                if (value != null && value.ToString()!.ToLower().Contains(lowerSearch))
                {
                    return true;
                }
            }
            return false;
        });
    }

    /// <summary>
    /// Applies a single filter to rows.
    /// </summary>
    private IEnumerable<object> ApplyFilter(IEnumerable<object> rows, FilterDef filter)
    {
        return rows.Where(row => MatchesFilter(row, filter));
    }

    /// <summary>
    /// Checks if a row matches a filter condition.
    /// </summary>
    private bool MatchesFilter(object row, FilterDef filter)
    {
        var value = GetPropertyValue(row, filter.PropertyName);

        return filter.Operator switch
        {
            FilterOperator.Equals => CompareEqual(value, filter.Value),
            FilterOperator.NotEquals => !CompareEqual(value, filter.Value),
            FilterOperator.Contains => value?.ToString()?.Contains(filter.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            FilterOperator.StartsWith => value?.ToString()?.StartsWith(filter.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            FilterOperator.EndsWith => value?.ToString()?.EndsWith(filter.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            FilterOperator.GreaterThan => CompareGreaterThan(value, filter.Value),
            FilterOperator.GreaterThanOrEquals => CompareGreaterThanOrEqual(value, filter.Value),
            FilterOperator.LessThan => CompareLessThan(value, filter.Value),
            FilterOperator.LessThanOrEquals => CompareLessThanOrEqual(value, filter.Value),
            FilterOperator.IsNull => value == null,
            FilterOperator.IsNotNull => value != null,
            FilterOperator.Between => CompareBetween(value, filter.Value, filter.Value2),
            _ => true
        };
    }

    /// <summary>
    /// Applies sorting to rows based on sort definitions.
    /// </summary>
    private List<object> ApplySort(List<object> rows, List<SortDef> sorts)
    {
        var ordered = rows.AsEnumerable();

        foreach (var sort in sorts)
        {
            ordered = sort.Descending
                ? ordered.OrderByDescending(row => GetPropertyValue(row, sort.PropertyName))
                : ordered.OrderBy(row => GetPropertyValue(row, sort.PropertyName));
        }

        return ordered.ToList();
    }

    /// <summary>
    /// Gets a property value from a dynamic object row using reflection.
    /// </summary>
    private object? GetPropertyValue(object row, string propertyName)
    {
        try
        {
            if (row == null)
                return null;

            var type = row.GetType();
            var property = type.GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

            if (property == null)
                return null;

            return property.GetValue(row);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compares two values for equality.
    /// </summary>
    private bool CompareEqual(object? value, object? filterValue)
    {
        if (value == null && filterValue == null)
            return true;

        if (value == null || filterValue == null)
            return false;

        return value.Equals(filterValue);
    }

    /// <summary>
    /// Compares two values for greater than.
    /// </summary>
    private bool CompareGreaterThan(object? value, object? filterValue)
    {
        if (value == null || filterValue == null)
            return false;

        return Compare(value, filterValue) > 0;
    }

    /// <summary>
    /// Compares two values for greater than or equal.
    /// </summary>
    private bool CompareGreaterThanOrEqual(object? value, object? filterValue)
    {
        if (value == null || filterValue == null)
            return false;

        return Compare(value, filterValue) >= 0;
    }

    /// <summary>
    /// Compares two values for less than.
    /// </summary>
    private bool CompareLessThan(object? value, object? filterValue)
    {
        if (value == null || filterValue == null)
            return false;

        return Compare(value, filterValue) < 0;
    }

    /// <summary>
    /// Compares two values for less than or equal.
    /// </summary>
    private bool CompareLessThanOrEqual(object? value, object? filterValue)
    {
        if (value == null || filterValue == null)
            return false;

        return Compare(value, filterValue) <= 0;
    }

    /// <summary>
    /// Compares two values for between.
    /// </summary>
    private bool CompareBetween(object? value, object? min, object? max)
    {
        if (value == null || min == null || max == null)
            return false;

        return Compare(value, min) >= 0 && Compare(value, max) <= 0;
    }

    /// <summary>
    /// Generic comparison for IComparable values.
    /// </summary>
    private int Compare(object? a, object? b)
    {
        if (a == null && b == null)
            return 0;

        if (a == null)
            return -1;

        if (b == null)
            return 1;

        if (a is IComparable comparableA)
        {
            return comparableA.CompareTo(b);
        }

        return a.ToString()!.CompareTo(b.ToString()!);
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
}
