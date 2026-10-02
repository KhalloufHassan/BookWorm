using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.Mobile.Core.Auth;
using Microsoft.Maui.Storage;

namespace BookWorm.Mobile.Services;

/// <summary>The server address in the app's preferences; the session (tokens) in Android's secure storage.</summary>
public sealed class SecureSessionStorage : ISessionStorage
{
    private const string ServerKey = "bookworm.server";
    private const string SessionKey = "bookworm.session";

    public Task<string> GetServerAddressAsync() => Task.FromResult(Preferences.Default.Get<string>(ServerKey, null));

    public Task SetServerAddressAsync(string address)
    {
        Preferences.Default.Set(ServerKey, address);
        return Task.CompletedTask;
    }

    public async Task<AppSession> LoadSessionAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(SessionKey);
            return json is null ? null : JsonSerializer.Deserialize<AppSession>(json, BookWormJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException
#if ANDROID
            or Java.Lang.Exception
#endif
        )
        {
            // Unreadable, e.g. after the app's data was restored to another phone: sign in again.
            SecureStorage.Default.Remove(SessionKey);
            return null;
        }
    }

    public Task SaveSessionAsync(AppSession session) =>
        SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(session, BookWormJson.Options));

    public Task ClearSessionAsync()
    {
        SecureStorage.Default.Remove(SessionKey);
        return Task.CompletedTask;
    }
}
