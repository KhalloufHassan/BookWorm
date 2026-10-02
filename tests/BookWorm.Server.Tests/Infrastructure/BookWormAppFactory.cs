using BookWorm.Contracts;
using BookWorm.Server.Auth;
using BookWorm.Server.Backups;
using BookWorm.Server.Data;
using BookWorm.UI.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(BookWorm.Server.Tests.Infrastructure.BookWormAppFactory))]

namespace BookWorm.Server.Tests.Infrastructure;

/// <summary>
/// Hosts the real server against a throwaway PostgreSQL 18 container, shared by all tests.
/// Tests stay independent by each working as their own freshly created user.
/// </summary>
public sealed class BookWormAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestPassword = "correct horse battery staple";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:18").Build();

    /// <summary>Book files, covers, notes and backups of this test run.</summary>
    public string DataFolder { get; } = Path.Combine(Path.GetTempPath(), $"bookworm-tests-{Guid.NewGuid():N}");

    public RecordingRestarter Restarter { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();
        // Starting the host runs the migrations.
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _database.DisposeAsync();
            if (Directory.Exists(DataFolder))
            {
                Directory.Delete(DataFolder, recursive: true);
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Database", _database.GetConnectionString());
        builder.UseSetting("Storage:DataPath", Path.Combine(DataFolder, "data"));
        builder.UseSetting("Backups:Path", Path.Combine(DataFolder, "backups"));
        builder.UseSetting("Backups:SchedulerEnabled", "false");
        // pg_dump and friends run inside the test database's container.
        builder.UseSetting("Backups:PostgresContainer", _database.Id);

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, options =>
                    // Requests without the test header use the real cookie and bearer token sign-in.
                    options.ForwardDefaultSelector = context =>
                        context.Request.Headers.ContainsKey(TestAuthHandler.UserHeader) ? null : AuthSchemes.CookieOrBearer);

            // Restores normally stop the app; tests only record that they asked to.
            services.RemoveAll<IAppRestarter>();
            services.AddSingleton<IAppRestarter>(Restarter);
        });
    }

    /// <summary>An API client with no sign-in, like a freshly installed app.</summary>
    public BookWormApiClient CreateAnonymousApi() => new(CreateClient());

    /// <summary>Creates a user account and an API client that is signed in as that user.</summary>
    public async Task<TestUser> CreateUserAsync(bool isAdmin = false)
    {
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser($"reader-{Guid.NewGuid():N}"[..20]);
        var created = await userManager.CreateAsync(user, TestPassword);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));

        if (isAdmin)
        {
            var promoted = await userManager.AddToRoleAsync(user, RoleNames.Admin);
            Assert.True(promoted.Succeeded, string.Join(" ", promoted.Errors.Select(e => e.Description)));
        }

        var http = CreateSignedInClient(user.Id, isAdmin);
        return new TestUser(user.Id, user.UserName, http, new BookWormApiClient(http));
    }

    public HttpClient CreateSignedInClient(Guid userId, bool isAdmin = false)
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        if (isAdmin)
        {
            http.DefaultRequestHeaders.Add(TestAuthHandler.AdminHeader, "true");
        }

        return http;
    }
}

public sealed record TestUser(Guid Id, string UserName, HttpClient Http, BookWormApiClient Api);

public sealed class RecordingRestarter : IAppRestarter
{
    public int Requests;

    public void RestartAfter(Microsoft.AspNetCore.Http.HttpResponse response) => Interlocked.Increment(ref Requests);
}
