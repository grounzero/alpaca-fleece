using Microsoft.JSInterop;

namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// JS interop for highlight.js syntax highlighting.
/// </summary>
public sealed class HighlightInterop(IJSRuntime js)
{
    public ValueTask HighlightElementAsync(string elementId, CancellationToken ct = default)
        => js.InvokeVoidAsync("highlightInterop.highlight", ct, elementId);
}
