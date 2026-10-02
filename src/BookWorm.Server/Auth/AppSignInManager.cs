using BookWorm.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BookWorm.Server.Auth;

/// <summary>
/// Identity's sign-in, plus the reason a passkey was refused (Identity only reports "failed"): it's
/// logged, and kept for the login page to show, e.g. when the passkey belongs to another address.
/// </summary>
public sealed class AppSignInManager(
    UserManager<AppUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<AppUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<AppUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<AppUser> confirmation)
    : SignInManager<AppUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    private readonly ILogger<SignInManager<AppUser>> _logger = logger;

    /// <summary>Why the last passkey sign-in in this request failed, or null.</summary>
    public string PasskeyFailure { get; private set; }

    public override async Task<PasskeyAssertionResult<AppUser>> PerformPasskeyAssertionAsync(string credentialJson)
    {
        var result = await base.PerformPasskeyAssertionAsync(credentialJson);
        if (!result.Succeeded)
        {
            PasskeyFailure = result.Failure?.Message;
            _logger.LogWarning("Passkey sign-in failed for {Host}: {Reason}", Context.Request.Host, PasskeyFailure);
        }

        return result;
    }
}
