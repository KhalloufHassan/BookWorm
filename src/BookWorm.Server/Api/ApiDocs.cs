using BookWorm.Contracts;
using Microsoft.AspNetCore.OpenApi;

namespace BookWorm.Server.Api;

/// <summary>The OpenAPI description at /openapi/bookworm.json, browsable at /scalar.</summary>
internal static class ApiDocs
{
    public const string DocumentName = "bookworm";

    public static void Configure(OpenApiOptions options)
    {
        // Only the /api endpoints, not the account pages' helper endpoints.
        options.ShouldInclude = description => description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;

        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "BookWorm API";
            document.Info.Version = $"{ApiLimits.CurrentApiVersion} (server {ServerVersion.Current})";
            document.Info.Description =
                "The API behind the BookWorm web app. Sign in through the web app first; " +
                "requests from this page then use your session cookie.";
            return Task.CompletedTask;
        });
    }
}
