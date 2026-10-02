using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace BookWorm.Server.Tests;

/// <summary>Sign-in for the mobile app: bearer tokens, two-factor codes, refresh and the browser (passkey) flow.</summary>
public sealed class AppAuthTests(BookWormAppFactory app)
{
    [Fact]
    public async Task Login_ReturnsTokens_ThatWorkOnTheApi()
    {
        var user = await app.CreateUserAsync();

        var tokens = await app.CreateAnonymousApi().LoginAsync(new AppLoginRequest { UserName = user.UserName, Password = BookWormAppFactory.TestPassword });

        Assert.Equal("Bearer", tokens.TokenType);
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        Assert.True(tokens.ExpiresIn > 0);
        var me = await ApiWith(tokens).GetCurrentUserAsync();
        Assert.Equal(user.Id, me?.Id);
    }

    [Fact]
    public async Task Login_WithAWrongPassword_Fails()
    {
        var user = await app.CreateUserAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() => app.CreateAnonymousApi()
            .LoginAsync(new AppLoginRequest { UserName = user.UserName, Password = "not the password" }));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(AuthProblemTypes.InvalidCredentials, error.Problem?.Type);
    }

    [Fact]
    public async Task Login_LocksTheAccount_AfterTooManyFailures()
    {
        var user = await app.CreateUserAsync();
        var api = app.CreateAnonymousApi();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync(new AppLoginRequest { UserName = user.UserName, Password = "wrong" }));
        }

        var error = await Assert.ThrowsAsync<ApiException>(() => api
            .LoginAsync(new AppLoginRequest { UserName = user.UserName, Password = BookWormAppFactory.TestPassword }));

        Assert.Equal(AuthProblemTypes.LockedOut, error.Problem?.Type);
    }

    [Fact]
    public async Task Login_WithTwoFactor_AsksForTheCode_ThenAcceptsIt()
    {
        var user = await app.CreateUserAsync();
        var key = await EnableTwoFactorAsync(user.Id);
        var api = app.CreateAnonymousApi();
        var request = new AppLoginRequest { UserName = user.UserName, Password = BookWormAppFactory.TestPassword };

        var needsCode = await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync(request));
        request.TwoFactorCode = "000000" == Totp.Code(key) ? "111111" : "000000";
        var wrongCode = await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync(request));
        request.TwoFactorCode = Totp.Code(key);
        var tokens = await api.LoginAsync(request);

        Assert.Equal(AuthProblemTypes.TwoFactorRequired, needsCode.Problem?.Type);
        Assert.Equal(AuthProblemTypes.InvalidTwoFactorCode, wrongCode.Problem?.Type);
        Assert.Equal(user.Id, (await ApiWith(tokens).GetCurrentUserAsync())?.Id);
    }

    [Fact]
    public async Task Login_WithTwoFactor_AcceptsARecoveryCode_Once()
    {
        var user = await app.CreateUserAsync();
        await EnableTwoFactorAsync(user.Id);
        string recoveryCode;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var account = await userManager.FindByIdAsync(user.Id.ToString());
            recoveryCode = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(account, 2)).First();
        }

        var api = app.CreateAnonymousApi();
        var request = new AppLoginRequest { UserName = user.UserName, Password = BookWormAppFactory.TestPassword, RecoveryCode = recoveryCode };

        await api.LoginAsync(request);
        var again = await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync(request));

        Assert.Equal(AuthProblemTypes.InvalidTwoFactorCode, again.Problem?.Type);
    }

    [Fact]
    public async Task Refresh_GivesNewTokens_UntilTheUserSignsOutEverywhere()
    {
        var user = await app.CreateUserAsync();
        var anonymous = app.CreateAnonymousApi();
        var tokens = await anonymous.LoginAsync(new AppLoginRequest { UserName = user.UserName, Password = BookWormAppFactory.TestPassword });

        var refreshed = await anonymous.RefreshTokensAsync(tokens.RefreshToken);
        await ApiWith(refreshed).SignOutEverywhereAsync();
        var error = await Assert.ThrowsAsync<ApiException>(() => anonymous.RefreshTokensAsync(refreshed.RefreshToken));

        Assert.NotEqual(tokens.AccessToken, refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task Refresh_RejectsGarbage()
    {
        var error = await Assert.ThrowsAsync<ApiException>(() => app.CreateAnonymousApi().RefreshTokensAsync("not a token"));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task ApiCalls_WithoutAValidToken_Get401_NotALoginRedirect()
    {
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        var withBadToken = await http.GetAsync("/api/books");
        http.DefaultRequestHeaders.Authorization = null;
        var withNothing = await http.GetAsync("/api/books");

        Assert.Equal(HttpStatusCode.Unauthorized, withBadToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withNothing.StatusCode);
    }

    [Fact]
    public async Task MobileCode_ExchangesForTokens_WithTheRightVerifier()
    {
        var user = await app.CreateUserAsync();
        var (verifier, code) = await IssueCodeAsync(user.Id);
        var api = app.CreateAnonymousApi();

        var wrong = await Assert.ThrowsAsync<ApiException>(() => api.RedeemMobileCodeAsync(new MobileCodeRequest { Code = code, CodeVerifier = NewVerifier() }));
        var tokens = await api.RedeemMobileCodeAsync(new MobileCodeRequest { Code = code, CodeVerifier = verifier });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(user.Id, (await ApiWith(tokens).GetCurrentUserAsync())?.Id);
    }

    [Fact]
    public async Task MobileCode_IsRejected_WhenTamperedExpiredOrOutOfDate()
    {
        var user = await app.CreateUserAsync();
        var api = app.CreateAnonymousApi();

        var (verifier, code) = await IssueCodeAsync(user.Id);
        var tampered = await Assert.ThrowsAsync<ApiException>(() =>
            api.RedeemMobileCodeAsync(new MobileCodeRequest { Code = code[..^4] + "AAAA", CodeVerifier = verifier }));

        var (oldVerifier, oldCode) = await IssueCodeAsync(user.Id, issuedAt: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        var expired = await Assert.ThrowsAsync<ApiException>(() =>
            api.RedeemMobileCodeAsync(new MobileCodeRequest { Code = oldCode, CodeVerifier = oldVerifier }));

        var (stampVerifier, stampCode) = await IssueCodeAsync(user.Id);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await userManager.UpdateSecurityStampAsync(await userManager.FindByIdAsync(user.Id.ToString()));
        }

        var outOfDate = await Assert.ThrowsAsync<ApiException>(() =>
            api.RedeemMobileCodeAsync(new MobileCodeRequest { Code = stampCode, CodeVerifier = stampVerifier }));

        Assert.All([tampered, expired, outOfDate], error => Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode));
    }

    [Fact]
    public async Task MobileSignInPage_SendsVisitorsToTheLoginPageFirst()
    {
        await app.CreateUserAsync(); // past the first-run setup redirect
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var challenge = MobileCodes.ChallengeFor(NewVerifier());

        var response = await http.GetAsync($"/Account/MobileLogin?challenge={challenge}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Uri.UnescapeDataString(response.Headers.Location.ToString());
        Assert.Contains("/Account/Login", location);
        Assert.Contains($"/Account/MobileLogin?challenge={challenge}", location);
    }

    [Fact]
    public async Task MobileSignInConfirmation_NeedsTheFormsAntiforgeryToken()
    {
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await http.PostAsync("/Account/MobileLogin/Confirm",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["challenge"] = MobileCodes.ChallengeFor(NewVerifier()) }));

        // Rejected (the page errors turn it into the "not found" page), and never sent on to the app.
        Assert.True((int)response.StatusCode is >= 400 and < 500, $"Got {response.StatusCode}");
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("https://0.0.0.1", true)]
    [InlineData("https://evil.example", false)]
    public async Task OnlyTheAppsWebView_MayCallTheApiFromJavaScript(string origin, bool allowed)
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, $"/api/books/{Guid.NewGuid()}/files/epub");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await app.CreateClient().SendAsync(preflight);

        Assert.Equal(allowed, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins) && origins.Single() == origin);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    private BookWormApiClient ApiWith(AppTokens tokens)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new BookWormApiClient(http);
    }

    private static string NewVerifier() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private async Task<(string Verifier, string Code)> IssueCodeAsync(Guid userId, DateTimeOffset? issuedAt = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var codes = issuedAt is { } time
            ? new MobileCodes(scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), new FixedTime(time))
            : scope.ServiceProvider.GetRequiredService<MobileCodes>();
        var verifier = NewVerifier();
        var code = await codes.IssueAsync(await userManager.FindByIdAsync(userId.ToString()), MobileCodes.ChallengeFor(verifier), userManager);
        return (verifier, code);
    }

    private async Task<string> EnableTwoFactorAsync(Guid userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        await userManager.ResetAuthenticatorKeyAsync(user);
        await userManager.SetTwoFactorEnabledAsync(user, true);
        return await userManager.GetAuthenticatorKeyAsync(user);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The current code of an authenticator app (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits).</summary>
    private static class Totp
    {
        public static string Code(string base32Key)
        {
            var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            var counter = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(counter);
            }

            var hash = HMACSHA1.HashData(Base32(base32Key), counter);
            var offset = hash[^1] & 0x0F;
            var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            return (value % 1_000_000).ToString("D6");
        }

        private static byte[] Base32(string text)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var c in text.TrimEnd('=').ToUpperInvariant())
            {
                buffer = (buffer << 5) | alphabet.IndexOf(c);
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)(buffer >> (bits - 8)));
                    bits -= 8;
                }
            }

            return [.. bytes];
        }
    }
}
