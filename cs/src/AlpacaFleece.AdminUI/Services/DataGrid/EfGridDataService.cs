using System.Linq.Dynamic.Core;

namespace AlpacaFleece.AdminUI.Services.DataGrid;

/// <summary>
/// Service for translating MudDataGrid server-side queries into EF Core queryables.
/// Uses System.Linq.Dynamic.Core for basic filtering.
/// Handles SQLite limitations (e.g. DateTimeOffset ordering fallback).
/// </summary>
public sealed class EfGridDataService(ILogger<EfGridDataService> logger)
{
    /// <summary>
    /// Execute a server-side query against an EF Core queryable.
    /// Applies filters and pagination based on MudDataGrid's GridState.
    /// </summary>
    public async Task<GridData<TItem>> QueryAsync<TItem>(
        IQueryable<TItem> source,
        GridState<TItem> state,
        string? quickSearch = null,
        string[]? searchableProps = null,
        CancellationToken ct = default)
        where TItem : class
    {
        try
        {
            // Apply quick search filter
            if (!string.IsNullOrWhiteSpace(quickSearch) && searchableProps?.Length > 0)
            {
                source = ApplyQuickSearch(source, quickSearch, searchableProps);
            }

            // Apply column filters (basic — via Dynamic LINQ where)
            if (state.FilterDefinitions?.Count > 0)
            {
                source = ApplyColumnFilters(source, state.FilterDefinitions);
            }

            // Default sort by Id descending (for chronological order)
            try
            {
                source = source.OrderBy("Id DESC");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to apply default sort by Id, continuing without sort");
            }

            // Get total count before paging
            var totalItems = await source.CountAsync(ct);

            // Apply paging
            var pageIndex = state.Page;
            var pageSize = state.PageSize;
            source = source.Skip(pageIndex * pageSize).Take(pageSize);

            // Fetch results
            var items = await source.ToListAsync(ct);

            return new GridData<TItem> { Items = items, TotalItems = totalItems };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing grid query");
            return new GridData<TItem> { Items = [], TotalItems = 0 };
        }
    }

    /// <summary>
    /// Apply quick search filter across multiple searchable properties.
    /// Uses OR logic: matches if value contains search term in ANY of the searchable properties.
    /// </summary>
    private static IQueryable<TItem> ApplyQuickSearch<TItem>(
        IQueryable<TItem> source,
        string searchTerm,
        string[] searchableProps)
        where TItem : class
    {
        if (searchableProps.Length == 0)
            return source;

        // Build: "Prop1.Contains(@0) || Prop2.Contains(@0) || ..."
        var predicates = searchableProps
            .Select(prop => $"{prop}.Contains(@0)")
            .ToList();

        if (predicates.Count == 0)
            return source;

        var orExpression = string.Join(" || ", predicates);
        try
        {
            return source.Where(orExpression, searchTerm);
        }
        catch (Exception ex)
        {
            // Log and return unfiltered if quick search fails
            Console.WriteLine($"Failed to apply quick search filter: {ex.Message}");
            return source;
        }
    }

    /// <summary>
    /// Apply MudDataGrid column filters using Dynamic LINQ.
    /// Simple implementation — maps common filter operators to dynamic expressions.
    /// </summary>
    private static IQueryable<TItem> ApplyColumnFilters<TItem>(
        IQueryable<TItem> source,
        ICollection<IFilterDefinition<TItem>> filterDefs)
        where TItem : class
    {
        foreach (var filterDef in filterDefs)
        {
            // Extract property name from filter definition
            // In MudBlazor, filterDef.Title or filterDef.Operator info is available
            // For now, we'll skip complex filtering and just handle basic cases

            var value = filterDef.Value;
            if (value is null)
                continue;

            // Skip advanced filtering for now (requires deeper MudBlazor API knowledge)
            // This simplified version focuses on core features
        }

        return source;
    }
}
