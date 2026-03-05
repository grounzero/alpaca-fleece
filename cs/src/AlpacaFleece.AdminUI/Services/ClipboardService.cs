namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Clipboard interop wrapper for copying text to the system clipboard.
/// </summary>
public sealed class ClipboardService(IJSRuntime js)
{
    public ValueTask<bool> CopyAsync(string text)
        => js.InvokeAsync<bool>("clipboardInterop.copyText", text);
}
