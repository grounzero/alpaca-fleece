namespace AlpacaFleece.AdminUI.Services;

/// <summary>
/// Blazor interop wrapper for browser localStorage.
/// </summary>
public sealed class LocalStorageService(IJSRuntime js)
{
    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default)
        => js.InvokeAsync<string?>("localStorageInterop.get", ct, key);

    public ValueTask SetAsync(string key, string value, CancellationToken ct = default)
        => js.InvokeVoidAsync("localStorageInterop.set", ct, key, value);

    public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        => js.InvokeVoidAsync("localStorageInterop.remove", ct, key);
}
