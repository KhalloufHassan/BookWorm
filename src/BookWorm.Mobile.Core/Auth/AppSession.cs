using BookWorm.Contracts;

namespace BookWorm.Mobile.Core.Auth;

/// <summary>
/// A signed-in session of the app: its tokens, and who is signed in (kept so the app still knows,
/// and can open downloaded books, while offline).
/// </summary>
public sealed record AppSession(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt, CurrentUser User);
