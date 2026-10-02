using BookWorm.Contracts;
using BookWorm.Server.Api;
using BookWorm.Server.Auth;
using BookWorm.Server.Backups;
using BookWorm.Server.Components;
using BookWorm.Server.Components.Account;
using BookWorm.Server.Data;
using BookWorm.Server.Notes;
using BookWorm.Server.Security;
using BookWorm.Server.Setup;
using BookWorm.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;

// "backup" and "restore <file>" run a command instead of the web app (see BackupCommands).
var (command, hostArguments) = BackupCommands.Split(args);

var builder = WebApplication.CreateBuilder(hostArguments);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddSingleton<SetupState>();

// Accounts and sign-in: ASP.NET Core Identity with cookies for the web app, and bearer tokens for
// the mobile app (see AuthEndpoints).
builder.Services.Configure<AppClientOptions>(builder.Configuration.GetSection(AppClientOptions.Section));
var appClientOptions = builder.Configuration.GetSection(AppClientOptions.Section).Get<AppClientOptions>() ?? new AppClientOptions();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = AuthSchemes.CookieOrBearer;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddPolicyScheme(AuthSchemes.CookieOrBearer, "Session cookie or bearer token", options => options.ForwardDefaultSelector = AuthSchemes.Select)
    .AddBearerToken(IdentityConstants.BearerScheme, options =>
    {
        options.BearerTokenExpiration = TimeSpan.FromMinutes(appClientOptions.AccessTokenMinutes);
        options.RefreshTokenExpiration = TimeSpan.FromDays(appClientOptions.RefreshTokenDays);
    })
    .AddIdentityCookies();
builder.Services.AddSingleton<MobileCodes>();
builder.Services.AddCors(options => options.AddPolicy(AppClientOptions.CorsPolicy, policy => policy
    .WithOrigins(appClientOptions.Origins)
    .WithHeaders("Authorization", "Content-Type")
    .WithMethods("GET", "PUT", "POST", "DELETE")));

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";

    // API calls get a status code instead of a redirect to the login page.
    options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
});

// Re-check sessions every minute, so password resets, role changes and deleted accounts apply quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin));

builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Database"),
        npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
    .UseSnakeCaseNamingConvention());

builder.Services.AddDataProtection()
    .SetApplicationName("BookWorm")
    .PersistKeysToDbContext<AppDbContext>();

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        // Accounts are created by an admin and email is optional, so there is nothing to confirm.
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = false;

        // Length over composition rules (NIST SP 800-63B).
        options.Password.RequiredLength = ApiLimits.PasswordMinLength;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager<AppSignInManager>()
    .AddDefaultTokenProviders();

// Book files, covers and exported notes on disk; backups.
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
builder.Services.AddSingleton<LibraryStorage>();
builder.Services.AddSingleton<NotesExporter>();
builder.Services.AddSingleton<INotesExportQueue>(services => services.GetRequiredService<NotesExporter>());
builder.Services.AddHostedService(services => services.GetRequiredService<NotesExporter>());
builder.Services.AddSingleton<PostgresTools>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<RestoreService>();
builder.Services.AddSingleton<IAppRestarter, HostAppRestarter>();
builder.Services.AddHostedService<BackupScheduler>();

// HTTP API
builder.Services.ConfigureHttpJsonOptions(options => BookWormJson.Configure(options.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(ApiDocs.DocumentName, ApiDocs.Configure);
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Database")))
{
    throw new InvalidOperationException(
        "No database is configured. Set the connection string 'ConnectionStrings:Database' " +
        "(environment variable ConnectionStrings__Database).");
}

if (command.Length > 0)
{
    return await BackupCommands.RunAsync(app, command);
}

app.Services.GetRequiredService<LibraryStorage>().Initialize();

// A restore requested from the app runs now, before anything uses the database or the files.
await app.Services.GetRequiredService<RestoreService>().RestorePendingAsync(CancellationToken.None);
await app.InitializeDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseHsts();
}

app.UseSecurityHeaders();

// API errors are answered with problem details (JSON); page errors with the error and not-found pages.
app.UseWhen(context => IsApiRequest(context), api =>
{
    api.UseExceptionHandler();
    api.UseStatusCodePages();
});
app.UseWhen(context => !IsApiRequest(context), pages =>
{
    if (!app.Environment.IsDevelopment())
    {
        pages.UseExceptionHandler("/Error", createScopeForErrors: true);
    }

    pages.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
});

// Before sign-in checks, so a fresh install goes straight to the setup page.
app.UseSetupRedirect();

// Explicitly after the error handling above, so rejected API calls (401/403) also get problem details.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(BookWorm.UI.Routes).Assembly);

app.MapAdditionalIdentityEndpoints().ExcludeFromDescription();
app.MapMobileSignIn();
app.MapBookWormApi();
app.MapHealthChecks("/health");
app.MapOpenApi();
app.MapScalarApiReference("/scalar", options => options
    .WithTitle("BookWorm API")
    .AddDocument(ApiDocs.DocumentName, "BookWorm API"));

app.Run();
return 0;

static bool IsApiRequest(HttpContext context) => context.Request.Path.StartsWithSegments("/api");

static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
{
    if (IsApiRequest(context.HttpContext))
    {
        context.Response.StatusCode = statusCode;
    }
    else
    {
        context.Response.Redirect(context.RedirectUri);
    }

    return Task.CompletedTask;
}

/// <summary>Entry point; public so integration tests can host the app.</summary>
public partial class Program;
