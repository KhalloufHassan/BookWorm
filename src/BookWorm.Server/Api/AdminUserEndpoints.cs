using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Data;
using BookWorm.Server.Notes;
using BookWorm.Server.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BookWorm.Server.Api;

/// <summary>
/// Minimal user management for administrators. There is no self-registration: an admin creates each
/// account, and resets passwords when someone forgets theirs.
/// </summary>
internal static class AdminUserEndpoints
{
    public static void MapAdminUserEndpoints(this IEndpointRouteBuilder api)
    {
        var users = api.MapGroup("/admin/users")
            .WithTags("Admin: users")
            .RequireAuthorization(Policies.Admin);

        users.MapGet("/", ListUsers).WithSummary("List all user accounts.");
        users.MapPost("/", CreateUser).WithValidation<CreateUserRequest>().WithSummary("Create a user account.");
        users.MapPut("/{id:guid}", UpdateUser).WithValidation<UpdateUserRequest>().WithSummary("Change a user's email or admin rights.");
        users.MapPut("/{id:guid}/password", ResetPassword).WithValidation<ResetPasswordRequest>()
            .WithSummary("Set a new password for a user and unlock their account.");
        users.MapDelete("/{id:guid}/two-factor", ResetTwoFactor)
            .WithSummary("Turn off a user's two-factor authentication, e.g. after they lost their authenticator app.");
        users.MapDelete("/{id:guid}", DeleteUser).WithSummary("Delete a user account and their whole library.");
    }

    private static async Task<Ok<List<AdminUser>>> ListUsers(
        UserManager<AppUser> userManager, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var adminIds = (await userManager.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var now = timeProvider.GetUtcNow();

        var users = await userManager.Users
            .AsNoTracking()
            .OrderBy(u => u.UserName)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(users.Select(u => ToAdminUser(u, adminIds.Contains(u.Id), now)).ToList());
    }

    private static async Task<Results<Created<AdminUser>, ValidationProblem>> CreateUser(
        CreateUserRequest request, UserManager<AppUser> userManager, TimeProvider timeProvider)
    {
        var user = new AppUser(request.UserName.Trim()) { Email = NormalizeEmail(request.Email) };

        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return IdentityProblem(created);
        }

        if (request.IsAdmin)
        {
            var promoted = await userManager.AddToRoleAsync(user, Roles.Admin);
            if (!promoted.Succeeded)
            {
                await userManager.DeleteAsync(user);
                return IdentityProblem(promoted);
            }
        }

        return TypedResults.Created($"/api/admin/users/{user.Id}", ToAdminUser(user, request.IsAdmin, timeProvider.GetUtcNow()));
    }

    private static async Task<Results<Ok<AdminUser>, NotFound, ValidationProblem>> UpdateUser(
        Guid id, UpdateUserRequest request, UserManager<AppUser> userManager, ICurrentUser currentUser, TimeProvider timeProvider)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        var isAdmin = await userManager.IsInRoleAsync(user, Roles.Admin);

        // Admins can't demote themselves, which also guarantees there is always at least one admin.
        if (isAdmin && !request.IsAdmin && id == currentUser.UserId)
        {
            return ApiErrors.Validation(nameof(UpdateUserRequest.IsAdmin), "You can't remove your own administrator rights.");
        }

        var email = NormalizeEmail(request.Email);
        if (email != user.Email)
        {
            var setEmail = await userManager.SetEmailAsync(user, email);
            if (!setEmail.Succeeded)
            {
                return IdentityProblem(setEmail);
            }
        }

        if (request.IsAdmin != isAdmin)
        {
            var changed = request.IsAdmin
                ? await userManager.AddToRoleAsync(user, Roles.Admin)
                : await userManager.RemoveFromRoleAsync(user, Roles.Admin);
            if (!changed.Succeeded)
            {
                return IdentityProblem(changed);
            }
        }

        return TypedResults.Ok(ToAdminUser(user, request.IsAdmin, timeProvider.GetUtcNow()));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> ResetPassword(
        Guid id, ResetPasswordRequest request, UserManager<AppUser> userManager)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        // Resetting also changes the security stamp, which signs the user out of their other sessions.
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!reset.Succeeded)
        {
            return IdentityProblem(reset);
        }

        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> ResetTwoFactor(
        Guid id, UserManager<AppUser> userManager)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        var disabled = await userManager.SetTwoFactorEnabledAsync(user, false);
        if (!disabled.Succeeded)
        {
            return IdentityProblem(disabled);
        }

        // A fresh key means the lost authenticator entry can never be used again, and the old
        // recovery codes go with it. This also changes the security stamp, signing out other sessions.
        var reset = await userManager.ResetAuthenticatorKeyAsync(user);
        if (!reset.Succeeded)
        {
            return IdentityProblem(reset);
        }

        await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> DeleteUser(
        Guid id, UserManager<AppUser> userManager, ICurrentUser currentUser, LibraryStorage storage, NotesExporter notesExporter)
    {
        if (id == currentUser.UserId)
        {
            return ApiErrors.Validation("id", "You can't delete your own account.");
        }

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        // The database removes the user's books, authors, tags and reads along with the account.
        var deleted = await userManager.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            return IdentityProblem(deleted);
        }

        // Then their book files, covers and exported notes.
        storage.DeleteUser(user.Id);
        notesExporter.DeleteUserFolder(user.Id, user.UserName);
        return TypedResults.NoContent();
    }

    private static AdminUser ToAdminUser(AppUser user, bool isAdmin, DateTimeOffset now) => new(
        user.Id,
        user.UserName,
        user.Email,
        isAdmin,
        user.TwoFactorEnabled,
        user.LockoutEnd > now,
        user.CreatedAt);

    private static string NormalizeEmail(string email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    /// <summary>Maps Identity's error codes onto the request fields they concern.</summary>
    private static ValidationProblem IdentityProblem(IdentityResult result) =>
        TypedResults.ValidationProblem(result.Errors
            .GroupBy(error => error.Code switch
            {
                _ when error.Code.StartsWith("Password", StringComparison.Ordinal) => "password",
                _ when error.Code.Contains("UserName", StringComparison.Ordinal) => "userName",
                _ when error.Code.Contains("Email", StringComparison.Ordinal) => "email",
                _ => "",
            })
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));
}
