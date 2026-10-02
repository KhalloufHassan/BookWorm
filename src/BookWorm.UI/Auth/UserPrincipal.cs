using System.Security.Claims;
using BookWorm.Contracts;

namespace BookWorm.UI.Auth;

/// <summary>The signed-in user as the claims the pages check (name, id, admin role).</summary>
public static class UserPrincipal
{
    public static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());

    public static ClaimsPrincipal From(CurrentUser user)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
        ];

        if (user.Email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (user.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, RoleNames.Admin));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "BookWorm"));
    }
}
