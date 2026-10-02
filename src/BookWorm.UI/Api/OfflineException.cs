namespace BookWorm.UI.Api;

/// <summary>
/// The server can't be reached and the request can't be answered from books downloaded to this
/// device. A kind of <see cref="HttpRequestException"/>, so pages handle it like any connection problem.
/// </summary>
public sealed class OfflineException(string message = OfflineException.DefaultMessage) : HttpRequestException(message)
{
    public const string DefaultMessage = "You're offline. Books you downloaded can still be read.";
}
