using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Setup;

/// <summary>
/// Tracks whether the first-run setup (creating the first administrator) still has to happen.
/// Once any user exists the answer is cached for the lifetime of the process.
/// </summary>
public sealed class SetupState(IServiceScopeFactory scopeFactory)
{
    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private volatile bool _hasUsers;

    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default)
    {
        if (_hasUsers)
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _hasUsers = await db.Users.AnyAsync(cancellationToken);
        return !_hasUsers;
    }

    /// <summary>
    /// Creates the first administrator. Fails if setup has already been completed, so this can't be
    /// used to add more admins later.
    /// </summary>
    public async Task<SetupResult> CreateFirstAdminAsync(
        UserManager<AppUser> userManager,
        string userName,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        await _setupLock.WaitAsync(cancellationToken);
        try
        {
            if (!await IsSetupRequiredAsync(cancellationToken))
            {
                return SetupResult.AlreadyCompleted;
            }

            var user = new AppUser(userName.Trim()) { Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim() };
            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                return SetupResult.Failed(created.Errors.Select(e => e.Description));
            }

            var promoted = await userManager.AddToRoleAsync(user, Roles.Admin);
            if (!promoted.Succeeded)
            {
                await userManager.DeleteAsync(user);
                return SetupResult.Failed(promoted.Errors.Select(e => e.Description));
            }

            _hasUsers = true;
            return SetupResult.Succeeded(user);
        }
        finally
        {
            _setupLock.Release();
        }
    }
}

public sealed record SetupResult(AppUser User, bool IsAlreadyCompleted, IReadOnlyList<string> Errors)
{
    public static SetupResult AlreadyCompleted { get; } = new(null, true, []);

    public static SetupResult Succeeded(AppUser user) => new(user, false, []);

    public static SetupResult Failed(IEnumerable<string> errors) => new(null, false, errors.ToList());
}
