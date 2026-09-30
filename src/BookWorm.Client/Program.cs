using BookWorm.Client.Services;
using BookWorm.UI.Api;
using BookWorm.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();

// The API is on the same origin, so the browser attaches the session cookie to every request.
builder.Services.AddScoped(services => new HttpClient(new SessionExpiredHandler(services.GetRequiredService<NavigationManager>())
{
    InnerHandler = new HttpClientHandler(),
})
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<BookWormApiClient>();

builder.Services.AddScoped<CookieAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(services => services.GetRequiredService<CookieAuthenticationStateProvider>());
builder.Services.AddScoped<IAccountService, WebAccountService>();
builder.Services.AddScoped<IPreferenceStore, LocalStoragePreferenceStore>();
builder.Services.AddScoped<BrowseState>();
builder.Services.AddScoped<ThemeState>();
builder.Services.AddScoped<BrowserFiles>();
builder.Services.AddScoped<PendingBook>();

await builder.Build().RunAsync();
