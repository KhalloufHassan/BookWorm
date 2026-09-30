using BookWorm.UI.Services;
using Microsoft.JSInterop;

namespace BookWorm.Client.Services;

/// <summary>Keeps per-device settings in the browser's local storage.</summary>
internal sealed class LocalStoragePreferenceStore(IJSRuntime js) : IPreferenceStore
{
    public ValueTask<string> GetAsync(string key) => js.InvokeAsync<string>("localStorage.getItem", key);

    public ValueTask SetAsync(string key, string value) => value is null
        ? js.InvokeVoidAsync("localStorage.removeItem", key)
        : js.InvokeVoidAsync("localStorage.setItem", key, value);
}
