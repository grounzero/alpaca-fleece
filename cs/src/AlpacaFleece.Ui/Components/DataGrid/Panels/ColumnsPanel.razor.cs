using Microsoft.AspNetCore.Components;

namespace AlpacaFleece.Ui.Components.DataGrid;

/// <summary>
/// Panel component for showing/hiding columns in a data grid.
/// </summary>
public partial class ColumnsPanel : ComponentBase
{
    /// <summary>
    /// Gets or sets the columns to display.
    /// </summary>
    [Parameter]
    public IReadOnlyList<object> Columns { get; set; } = new List<object>();

    /// <summary>
    /// Gets or sets the callback when the panel is closed.
    /// </summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    private bool DrawerOpen = true;

    /// <summary>
    /// Handles closing the panel.
    /// </summary>
    private async Task OnCloseInternal()
    {
        DrawerOpen = false;
        await OnClose.InvokeAsync();
    }

    private async Task OnClose()
    {
        await OnCloseInternal();
    }
}
