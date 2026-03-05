using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using AlpacaFleece.AdminUI.Services.DataGrid;

namespace AlpacaFleece.AdminUI.Components.DataGrid;

/// <summary>
/// Reusable MudBlazor DataGrid component for viewing and editing any DbSet.
/// Supports sorting, filtering, grouping, pagination, virtualization, and state persistence.
/// </summary>
public partial class DbTableGrid<TItem> : ComponentBase where TItem : class
{
    /// <summary>
    /// Gets or sets the unique name for this table (used for state persistence).
    /// </summary>
    [Parameter]
    public string TableKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display title for the grid.
    /// </summary>
    [Parameter]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the column definitions.
    /// </summary>
    [Parameter]
    public IReadOnlyList<DbColumnDef<TItem>> Columns { get; set; } = new List<DbColumnDef<TItem>>();

    /// <summary>
    /// Gets or sets the server data loading function.
    /// </summary>
    [Parameter]
    public Func<GridQuery, Task<GridResult<TItem>>>? LoadDataAsync { get; set; }

    /// <summary>
    /// Gets or sets the create function (optional).
    /// </summary>
    [Parameter]
    public Func<TItem, Task>? CreateAsync { get; set; }

    /// <summary>
    /// Gets or sets the update function (optional).
    /// </summary>
    [Parameter]
    public Func<TItem, Task>? UpdateAsync { get; set; }

    /// <summary>
    /// Gets or sets the delete function (optional).
    /// </summary>
    [Parameter]
    public Func<TItem, Task>? DeleteAsync { get; set; }

    /// <summary>
    /// Gets or sets the function to get the row key from an item.
    /// </summary>
    [Parameter]
    public Func<TItem, object>? GetKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the grid is read-only.
    /// </summary>
    [Parameter]
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Gets or sets extra toolbar content.
    /// </summary>
    [Parameter]
    public RenderFragment? ToolBarExtraContent { get; set; }

    /// <summary>
    /// Gets or sets the row detail content template.
    /// </summary>
    [Parameter]
    public RenderFragment<TItem>? RowDetailContent { get; set; }

    /// <summary>
    /// Gets or sets the function to determine row CSS classes.
    /// </summary>
    [Parameter]
    public Func<TItem, int, string>? RowClassFunc { get; set; }

    /// <summary>
    /// Gets or sets the function to determine row styles.
    /// </summary>
    [Parameter]
    public Func<TItem, int, string>? RowStyleFunc { get; set; }

    /// <summary>
    /// Gets or sets the state store for persisting grid state.
    /// </summary>
    [Inject]
    public IGridStateStore? StateStore { get; set; }

    [Inject]
    public ISnackbar? Snackbar { get; set; }

    [Inject]
    public IDialogService? DialogService { get; set; }

    // Private fields
    private MudDataGrid<TItem>? Grid;
    private GridQuery CurrentQuery = new();
    private GridResult<TItem>? CurrentResult;
    private HashSet<TItem> SelectedItems = new();
    private HashSet<object> SelectedKeys = new();
    private TItem? EditingItem;
    private bool IsEditingNew;
    private bool IsLoading;
    private bool ShowColumnPanel;
    private DateTime LastSearchTime;
    private System.Timers.Timer? SearchDebounceTimer;

    protected override async Task OnInitializedAsync()
    {
        // Load persisted state if available
        if (StateStore != null && !string.IsNullOrEmpty(TableKey))
        {
            var state = await StateStore.LoadStateAsync(TableKey);
            if (state != null)
            {
                CurrentQuery.PageIndex = state.PageIndex;
                CurrentQuery.PageSize = state.PageSize;
                CurrentQuery.SearchText = state.SearchText;
                CurrentQuery.Sorts = state.Sorts;
                CurrentQuery.Filters = state.Filters;
                SelectedKeys = state.SelectedKeys;
            }
        }

        await LoadData();
    }

    /// <summary>
    /// Loads data from the server.
    /// </summary>
    private async Task LoadData()
    {
        if (LoadDataAsync == null)
        {
            return;
        }

        try
        {
            IsLoading = true;
            CurrentResult = await LoadDataAsync(CurrentQuery);
        }
        catch (Exception ex)
        {
            Snackbar?.Add($"Error loading data: {ex.Message}", Severity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Handles search input with debouncing.
    /// </summary>
    private void OnSearchKeyUp()
    {
        SearchDebounceTimer?.Stop();
        SearchDebounceTimer?.Dispose();

        SearchDebounceTimer = new System.Timers.Timer(500);
        SearchDebounceTimer.Elapsed += async (s, e) =>
        {
            SearchDebounceTimer?.Stop();
            CurrentQuery.PageIndex = 0;
            await InvokeAsync(async () =>
            {
                await LoadData();
                await PersistState();
            });
        };
        SearchDebounceTimer.AutoReset = false;
        SearchDebounceTimer.Start();
    }

    /// <summary>
    /// Handles row selection changes.
    /// </summary>
    private void OnSelectedItemsChanged(HashSet<TItem> items)
    {
        SelectedItems = items;
        SelectedKeys.Clear();
        if (GetKey != null)
        {
            foreach (var item in items)
            {
                SelectedKeys.Add(GetKey(item));
            }
        }
    }

    /// <summary>
    /// Refreshes the data from the server.
    /// </summary>
    private async Task RefreshData()
    {
        CurrentQuery.PageIndex = 0;
        await LoadData();
    }

    /// <summary>
    /// Clears all filters and searches.
    /// </summary>
    private async Task ClearFilters()
    {
        CurrentQuery.SearchText = null;
        CurrentQuery.Filters.Clear();
        CurrentQuery.PageIndex = 0;
        await LoadData();
    }

    /// <summary>
    /// Resets layout (column order, visibility, widths) to defaults.
    /// </summary>
    private async Task ResetLayout()
    {
        if (StateStore != null && !string.IsNullOrEmpty(TableKey))
        {
            await StateStore.DeleteStateAsync(TableKey);
        }

        ShowColumnPanel = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Toggles the columns panel visibility.
    /// </summary>
    private void ToggleColumnsPanel()
    {
        ShowColumnPanel = !ShowColumnPanel;
    }

    /// <summary>
    /// Opens the edit dialog for a new item.
    /// </summary>
    private async Task OpenCreateDialog()
    {
        // Create a new instance
        var newItem = Activator.CreateInstance<TItem>();
        EditingItem = newItem;
        IsEditingNew = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Opens the edit dialog for an existing item.
    /// </summary>
    private async Task OpenEditDialog(TItem item)
    {
        EditingItem = item;
        IsEditingNew = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Closes the edit dialog without saving.
    /// </summary>
    private async Task CloseEditDialog()
    {
        EditingItem = null;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Saves the edited item.
    /// </summary>
    private async Task SaveEditingItem(TItem item)
    {
        try
        {
            if (IsEditingNew && CreateAsync != null)
            {
                await CreateAsync(item);
                Snackbar?.Add("Record created successfully", Severity.Success);
            }
            else if (!IsEditingNew && UpdateAsync != null)
            {
                await UpdateAsync(item);
                Snackbar?.Add("Record updated successfully", Severity.Success);
            }

            await CloseEditDialog();
            await RefreshData();
        }
        catch (Exception ex)
        {
            Snackbar?.Add($"Error saving record: {ex.Message}", Severity.Error);
        }
    }

    /// <summary>
    /// Deletes a single row.
    /// </summary>
    private async Task DeleteRow(TItem item)
    {
        var confirm = await DialogService!.ShowAsync<ConfirmDialog>("Confirm Delete", new DialogParameters { });
        var result = await confirm.Result;

        if (!result.Canceled && DeleteAsync != null)
        {
            try
            {
                await DeleteAsync(item);
                Snackbar?.Add("Record deleted successfully", Severity.Success);
                await RefreshData();
            }
            catch (Exception ex)
            {
                Snackbar?.Add($"Error deleting record: {ex.Message}", Severity.Error);
            }
        }
    }

    /// <summary>
    /// Deletes the selected rows.
    /// </summary>
    private async Task DeleteSelectedRows()
    {
        if (SelectedItems.Count == 0 || DeleteAsync == null)
        {
            return;
        }

        var confirm = await DialogService!.ShowAsync<ConfirmDialog>(
            "Confirm Delete",
            new DialogParameters { { "ContentText", $"Delete {SelectedItems.Count} records?" } });
        var result = await confirm.Result;

        if (!result.Canceled)
        {
            try
            {
                foreach (var item in SelectedItems)
                {
                    await DeleteAsync(item);
                }

                Snackbar?.Add($"{SelectedItems.Count} records deleted successfully", Severity.Success);
                SelectedItems.Clear();
                SelectedKeys.Clear();
                await RefreshData();
            }
            catch (Exception ex)
            {
                Snackbar?.Add($"Error deleting records: {ex.Message}", Severity.Error);
            }
        }
    }

    /// <summary>
    /// Persists the current grid state to storage.
    /// </summary>
    private async Task PersistState()
    {
        if (StateStore == null || string.IsNullOrEmpty(TableKey))
        {
            return;
        }

        var state = new GridState
        {
            PageIndex = CurrentQuery.PageIndex,
            PageSize = CurrentQuery.PageSize,
            SearchText = CurrentQuery.SearchText,
            Sorts = CurrentQuery.Sorts,
            Filters = CurrentQuery.Filters,
            SelectedKeys = SelectedKeys
        };

        await StateStore.SaveStateAsync(TableKey, state);
    }

    /// <summary>
    /// Gets a property expression for dynamic property access.
    /// </summary>
    private Expression<Func<TItem, object>> GetPropertyExpression(string propertyName)
    {
        var parameter = Expression.Parameter(typeof(TItem), "x");
        var property = Expression.Property(parameter, propertyName);
        var objectCast = Expression.Convert(property, typeof(object));
        var lambda = Expression.Lambda<Func<TItem, object>>(objectCast, parameter);
        return lambda;
    }

    public void Dispose()
    {
        SearchDebounceTimer?.Dispose();
    }
}
