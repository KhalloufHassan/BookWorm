using Android.App;
using Android.Content;
using Android.Content.PM;
using Microsoft.Maui.Authentication;

namespace BookWorm.Mobile;

/// <summary>
/// Receives bookworm://auth?code=… from the browser at the end of the browser sign-in (see
/// MobileAppSignIn) and hands it back to the waiting WebAuthenticator. The scheme and host must
/// match MobileSignIn.CallbackUri.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "bookworm",
    DataHost = "auth")]
public class SignInCallbackActivity : WebAuthenticatorCallbackActivity
{
}
