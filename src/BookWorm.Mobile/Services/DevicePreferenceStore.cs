using BookWorm.UI.Services;
using Microsoft.Maui.Storage;

namespace BookWorm.Mobile.Services;

/// <summary>Per-device settings (theme, reader settings…) in the app's preferences.</summary>
public sealed class DevicePreferenceStore : IPreferenceStore
{
    public ValueTask<string> GetAsync(string key) => ValueTask.FromResult(Preferences.Default.Get<string>(key, null));

    public ValueTask SetAsync(string key, string value)
    {
        if (value is null)
        {
            Preferences.Default.Remove(key);
        }
        else
        {
            Preferences.Default.Set(key, value);
        }

        return ValueTask.CompletedTask;
    }
}
