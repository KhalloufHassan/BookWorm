using System.Net;
using BookWorm.UI.Api;

namespace BookWorm.UI.Components;

public static class ApiErrorMessages
{
    /// <summary>A sentence to show the user when an API call fails.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        ApiException { StatusCode: HttpStatusCode.Conflict } api => api.Problem?.Detail ?? "Someone changed this in the meantime. Reload and try again.",
        ApiException { StatusCode: HttpStatusCode.NotFound } => "This item doesn't exist anymore.",
        ApiException { StatusCode: HttpStatusCode.Forbidden } => "You're not allowed to do that.",
        ApiException { StatusCode: HttpStatusCode.BadRequest } api when api.Errors.Count > 0 =>
            string.Join(" ", api.Errors.Values.SelectMany(messages => messages)),
        ApiException api => api.Message,
        HttpRequestException => "Can't reach the BookWorm server. Check your connection and try again.",
        _ => "Something went wrong. Please try again.",
    };
}
