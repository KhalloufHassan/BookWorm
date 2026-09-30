using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookWorm.Contracts;

/// <summary>One page of a list endpoint's results.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public enum SortDirection
{
    Asc,
    Desc,
}

/// <summary>RFC 9457 problem details, the body of every API error response.</summary>
public sealed class ApiProblem
{
    public string Type { get; set; }
    public string Title { get; set; }
    public int? Status { get; set; }
    public string Detail { get; set; }

    /// <summary>Validation errors keyed by camelCase field name.</summary>
    public Dictionary<string, string[]> Errors { get; set; }
}

/// <summary>
/// Public information about the server. Apps call this first to check that they can talk to it:
/// <see cref="ApiVersion"/> only changes when the API changes in a way that breaks existing clients.
/// </summary>
public sealed record ServerInfo(string Name, string Version, int ApiVersion, bool SetupRequired);

public static class ApiLimits
{
    public const int CurrentApiVersion = 1;

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public const int TitleMaxLength = 500;
    public const int AuthorNameMaxLength = 300;
    public const int TagNameMaxLength = 100;
    public const int NotesMaxLength = 1_000_000;
    public const int MaxAuthorsPerBook = 50;
    public const int MaxTagsPerBook = 100;
    public const int MaxSearchLength = 200;

    public const int LocationMaxLength = 2048;
    public const int HighlightTextMaxLength = 20_000;
    public const int HighlightContextMaxLength = 200;
    public const int HighlightNoteMaxLength = 100_000;
    public const int ChapterMaxLength = 500;
    public const int PageLabelMaxLength = 64;
    public const int FileNameMaxLength = 255;
    public const long CoverMaxBytes = 10 * 1024 * 1024;

    public const int UserNameMaxLength = 64;
    public const int EmailMaxLength = 256;
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 256;
}

/// <summary>JSON settings shared by the server and every client, so both sides agree on the wire format.</summary>
public static class BookWormJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
