using MudBlazor;

namespace BookWorm.UI.Services;

public enum ThemeChoice
{
    Device,
    Light,
    Dark,
}

/// <summary>
/// The app's light or dark look, chosen per device (or following the device's own setting). Shared
/// by the main layout and the reader, which have their own theme providers. The reader has its own
/// light or dark choice, which overrides the app's while a book is open.
/// </summary>
public sealed class ThemeState(IPreferenceStore preferences)
{
    private MudThemeProvider _provider;
    private bool _loaded;

    public ThemeChoice Choice { get; private set; } = ThemeChoice.Device;

    public bool IsDarkMode { get; private set; }

    /// <summary>Set by the reader while a book is open; null elsewhere.</summary>
    public bool? ReaderIsDark { get; private set; }

    /// <summary>What the theme provider shows: the reader's choice while a book is open, else the app's.</summary>
    public bool ShowsDark => ReaderIsDark ?? IsDarkMode;

    public event Action Changed;

    /// <summary>Called by each layout's theme provider once it has rendered.</summary>
    public async Task AttachAsync(MudThemeProvider provider)
    {
        _provider = provider;
        if (!_loaded)
        {
            _loaded = true;
            Choice = await preferences.GetAsync(PreferenceKeys.Theme) switch
            {
                "light" => ThemeChoice.Light,
                "dark" => ThemeChoice.Dark,
                _ => ThemeChoice.Device,
            };
        }

        await provider.WatchSystemDarkModeAsync(OnDeviceDarkModeChangedAsync);
        await ApplyAsync();
    }

    public async Task SetChoiceAsync(ThemeChoice choice)
    {
        Choice = choice;
        await preferences.SetAsync(PreferenceKeys.Theme, choice switch
        {
            ThemeChoice.Light => "light",
            ThemeChoice.Dark => "dark",
            _ => null,
        });
        await ApplyAsync();
    }

    public void SetReaderDarkMode(bool? isDark)
    {
        if (ReaderIsDark != isDark)
        {
            ReaderIsDark = isDark;
            Changed?.Invoke();
        }
    }

    private async Task ApplyAsync()
    {
        IsDarkMode = Choice switch
        {
            ThemeChoice.Light => false,
            ThemeChoice.Dark => true,
            _ => _provider is not null && await _provider.GetSystemDarkModeAsync(),
        };
        Changed?.Invoke();
    }

    private Task OnDeviceDarkModeChangedAsync(bool isDark)
    {
        if (Choice == ThemeChoice.Device)
        {
            IsDarkMode = isDark;
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }
}
