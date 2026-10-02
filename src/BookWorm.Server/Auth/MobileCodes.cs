using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookWorm.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace BookWorm.Server.Auth;

/// <summary>
/// One-time codes for the mobile app's browser sign-in (PKCE). A code is the user id, security stamp
/// and code challenge, encrypted by data protection and valid for two minutes, so no table is needed.
/// It only turns into tokens together with the verifier the app kept to itself.
/// </summary>
public sealed class MobileCodes(IDataProtectionProvider dataProtection, TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("BookWorm.MobileCode").ToTimeLimitedDataProtector();

    /// <summary>A PKCE S256 challenge: the base64url SHA-256 of the verifier, 43 characters.</summary>
    public static bool IsValidChallenge(string challenge) =>
        challenge is { Length: 43 } && challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string ChallengeFor(string verifier) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task<string> IssueAsync(AppUser user, string challenge, UserManager<AppUser> userManager)
    {
        var payload = new CodePayload(user.Id, await userManager.GetSecurityStampAsync(user), challenge);
        return _protector.Protect(JsonSerializer.Serialize(payload), timeProvider.GetUtcNow() + Lifetime);
    }

    /// <summary>The user the code was issued to, or null when the code or verifier is wrong, expired or out of date.</summary>
    public async Task<AppUser> RedeemAsync(string code, string verifier, UserManager<AppUser> userManager)
    {
        CodePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<CodePayload>(_protector.Unprotect(code, out var expiration));
            if (expiration < timeProvider.GetUtcNow())
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }

        var expected = Encoding.ASCII.GetBytes(payload.Challenge ?? "");
        var actual = Encoding.ASCII.GetBytes(ChallengeFor(verifier));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return null;
        }

        // The stamp changes when the password changes or the user signs out everywhere.
        var user = await userManager.FindByIdAsync(payload.UserId.ToString());
        return user is not null && await userManager.GetSecurityStampAsync(user) == payload.SecurityStamp ? user : null;
    }

    private sealed record CodePayload(Guid UserId, string SecurityStamp, string Challenge);
}
