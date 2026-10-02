using System.Security.Cryptography;
using System.Text;

namespace BookWorm.Mobile.Core.Auth;

/// <summary>PKCE (RFC 7636, S256) for the browser sign-in: the verifier stays in the app, only its hash travels.</summary>
public static class Pkce
{
    public static (string Verifier, string Challenge) Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, ChallengeFor(verifier));
    }

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
