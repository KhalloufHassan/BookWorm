using System.Net;
using BookWorm.Contracts;
using BookWorm.Server.Data;
using BookWorm.Server.Tests.Infrastructure;
using BookWorm.UI.Api;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookWorm.Server.Tests;

public sealed class AdminUsersApiTests(BookWormAppFactory app)
{
    [Fact]
    public async Task RegularUsers_CannotManageAccounts()
    {
        var user = await app.CreateUserAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() => user.Api.GetUsersAsync());

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task Admins_CanCreateUpdateAndResetAccounts()
    {
        var admin = (await app.CreateUserAsync(isAdmin: true)).Api;
        var userName = $"new-{Guid.NewGuid():N}"[..16];

        var created = await admin.CreateUserAsync(new CreateUserRequest { UserName = userName, Password = "first password" });
        Assert.Null(created.Email);
        Assert.False(created.IsAdmin);
        Assert.Contains(await admin.GetUsersAsync(), u => u.Id == created.Id);

        var updated = await admin.UpdateUserAsync(created.Id, new UpdateUserRequest { Email = "reader@example.com", IsAdmin = true });
        Assert.Equal("reader@example.com", updated.Email);
        Assert.True(updated.IsAdmin);

        await admin.ResetPasswordAsync(created.Id, new ResetPasswordRequest { NewPassword = "second password" });

        await using var scope = app.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var stored = await userManager.FindByIdAsync(created.Id.ToString());
        Assert.True(await userManager.CheckPasswordAsync(stored, "second password"));
        Assert.True(await userManager.IsInRoleAsync(stored, RoleNames.Admin));
    }

    [Fact]
    public async Task CreateUser_ReportsInvalidInputPerField()
    {
        var admin = (await app.CreateUserAsync(isAdmin: true)).Api;
        var existing = await app.CreateUserAsync();

        var duplicate = await Assert.ThrowsAsync<ApiException>(() =>
            admin.CreateUserAsync(new CreateUserRequest { UserName = existing.UserName, Password = "long enough" }));
        var shortPassword = await Assert.ThrowsAsync<ApiException>(() =>
            admin.CreateUserAsync(new CreateUserRequest { UserName = "shorty", Password = "short" }));
        var badEmail = await Assert.ThrowsAsync<ApiException>(() =>
            admin.CreateUserAsync(new CreateUserRequest { UserName = "emailer", Email = "not-an-email", Password = "long enough" }));

        Assert.Contains("userName", duplicate.Errors.Keys);
        Assert.Contains("password", shortPassword.Errors.Keys);
        Assert.Contains("email", badEmail.Errors.Keys);
    }

    [Fact]
    public async Task Admins_CannotDeleteOrDemoteThemselves()
    {
        var admin = await app.CreateUserAsync(isAdmin: true);

        var delete = await Assert.ThrowsAsync<ApiException>(() => admin.Api.DeleteUserAsync(admin.Id));
        var demote = await Assert.ThrowsAsync<ApiException>(() =>
            admin.Api.UpdateUserAsync(admin.Id, new UpdateUserRequest { IsAdmin = false }));

        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
    }

    [Fact]
    public async Task Admins_CanTurnOffAUsersTwoFactorAuthentication()
    {
        var admin = (await app.CreateUserAsync(isAdmin: true)).Api;
        var user = await app.CreateUserAsync();

        string oldKey;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var stored = await userManager.FindByIdAsync(user.Id.ToString());
            await userManager.ResetAuthenticatorKeyAsync(stored);
            await userManager.SetTwoFactorEnabledAsync(stored, true);
            await userManager.GenerateNewTwoFactorRecoveryCodesAsync(stored, 10);
            oldKey = await userManager.GetAuthenticatorKeyAsync(stored);
        }

        Assert.True((await admin.GetUsersAsync()).Single(u => u.Id == user.Id).TwoFactorEnabled);

        await admin.ResetTwoFactorAsync(user.Id);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var stored = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.False(await userManager.GetTwoFactorEnabledAsync(stored));
            Assert.NotEqual(oldKey, await userManager.GetAuthenticatorKeyAsync(stored));
            Assert.Equal(0, await userManager.CountRecoveryCodesAsync(stored));
        }

        Assert.False((await admin.GetUsersAsync()).Single(u => u.Id == user.Id).TwoFactorEnabled);
    }

    [Fact]
    public async Task RegularUsers_CannotTurnOffTwoFactorAuthentication()
    {
        var user = await app.CreateUserAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() => user.Api.ResetTwoFactorAsync(user.Id));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task DeletingAUser_DeletesTheirWholeLibrary()
    {
        var admin = (await app.CreateUserAsync(isAdmin: true)).Api;
        var leaving = await app.CreateUserAsync();
        var author = await leaving.Api.AddAuthorAsync("Their author");
        var tag = await leaving.Api.AddTagAsync("Their tag");
        var book = await leaving.Api.AddBookAsync("Their book", [author.Id], [tag.Id]);
        await leaving.Api.CreateReadAsync(book.Id, new CreateReadRequest());

        await admin.DeleteUserAsync(leaving.Id);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Id == leaving.Id));
        Assert.False(await db.Books.IgnoreQueryFilters().AnyAsync(b => b.UserId == leaving.Id));
        Assert.False(await db.Authors.IgnoreQueryFilters().AnyAsync(a => a.UserId == leaving.Id));
        Assert.False(await db.Tags.IgnoreQueryFilters().AnyAsync(t => t.UserId == leaving.Id));
        Assert.False(await db.Reads.IgnoreQueryFilters().AnyAsync(r => r.BookId == book.Id));
    }
}
