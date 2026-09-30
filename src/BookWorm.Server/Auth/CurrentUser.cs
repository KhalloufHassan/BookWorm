using System.Security.Claims;
using BookWorm.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BookWorm.Server.Auth;

public interface ICurrentUser
{
    /// <summary>The signed-in user's id, or null when nobody is signed in.</summary>
    Guid? UserId { get; }
}

internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor, IOptions<IdentityOptions> identityOptions) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(identityOptions.Value.ClaimsIdentity.UserIdClaimType), out var id)
            ? id
            : null;
}

public static class Roles
{
    public const string Admin = RoleNames.Admin;
}

public static class Policies
{
    public const string Admin = RoleNames.Admin;
}
