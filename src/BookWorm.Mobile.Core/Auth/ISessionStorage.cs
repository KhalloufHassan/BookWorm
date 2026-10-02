namespace BookWorm.Mobile.Core.Auth;

/// <summary>Where the app keeps its server address and session (secure storage on the phone).</summary>
public interface ISessionStorage
{
    Task<string> GetServerAddressAsync();

    Task SetServerAddressAsync(string address);

    /// <summary>The saved session, or null when signed out.</summary>
    Task<AppSession> LoadSessionAsync();

    Task SaveSessionAsync(AppSession session);

    Task ClearSessionAsync();
}
