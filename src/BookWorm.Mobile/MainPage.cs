using BookWorm.Mobile.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace BookWorm.Mobile;

/// <summary>The app's only page: a web view showing BookWorm's pages (BookWorm.UI).</summary>
public sealed class MainPage : ContentPage
{
    private readonly BlazorWebView _webView;

    public MainPage(WebViewRequests requests)
    {
        _webView = new BlazorWebView { HostPage = "wwwroot/index.html" };
        _webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(BookWorm.UI.Routes) });
        _webView.WebResourceRequested += requests.OnWebResourceRequested;
        BackgroundColor = Color.FromArgb("#F7F1E6");
        Content = _webView;
    }

    /// <summary>Android's back button goes back in the app, and only leaves it from the first page.</summary>
    protected override bool OnBackButtonPressed()
    {
#if ANDROID
        if (_webView.Handler?.PlatformView is Android.Webkit.WebView web && web.CanGoBack())
        {
            web.GoBack();
            return true;
        }
#endif
        return base.OnBackButtonPressed();
    }
}
