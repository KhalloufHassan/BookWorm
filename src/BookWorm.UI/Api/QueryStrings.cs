using System.Globalization;
using BookWorm.Contracts;

namespace BookWorm.UI.Api;

/// <summary>Builds query strings in the format the API binds (repeated keys for lists, ISO dates).</summary>
internal static class QueryStrings
{
    public static string For(BookListQuery query)
    {
        if (query is null)
        {
            return "";
        }

        return new Builder()
            .Add("search", query.Search)
            .AddEach("status", query.Status)
            .AddEach("tagIds", query.TagIds)
            .AddEach("authorIds", query.AuthorIds)
            .Add("ratingMin", query.RatingMin)
            .Add("ratingMax", query.RatingMax)
            .Add("publishedFrom", query.PublishedFrom)
            .Add("publishedTo", query.PublishedTo)
            .Add("sort", query.Sort)
            .Add("direction", query.Direction)
            .Add("page", query.Page)
            .Add("pageSize", query.PageSize)
            .ToString();
    }

    public static string For(AuthorListQuery query)
    {
        if (query is null)
        {
            return "";
        }

        return new Builder()
            .Add("search", query.Search)
            .Add("bornFrom", query.BornFrom)
            .Add("bornTo", query.BornTo)
            .Add("diedFrom", query.DiedFrom)
            .Add("diedTo", query.DiedTo)
            .Add("sort", query.Sort)
            .Add("direction", query.Direction)
            .Add("page", query.Page)
            .Add("pageSize", query.PageSize)
            .ToString();
    }

    public static string ForSearch(string search) => new Builder().Add("search", search).ToString();

    public static string Build(params (string Key, string Value)[] parameters)
    {
        var builder = new Builder();
        foreach (var (key, value) in parameters)
        {
            builder.Add(key, value);
        }

        return builder.ToString();
    }

    private sealed class Builder
    {
        private readonly List<string> _parts = [];

        public Builder Add(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }

            return this;
        }

        public Builder Add<T>(string key, T? value) where T : struct =>
            value is { } v ? Add(key, Format(v)) : this;

        public Builder AddEach<T>(string key, IEnumerable<T> values) where T : struct
        {
            foreach (var value in values ?? [])
            {
                Add(key, Format(value));
            }

            return this;
        }

        public override string ToString() => _parts.Count == 0 ? "" : "?" + string.Join('&', _parts);

        private static string Format<T>(T value) where T : struct => value switch
        {
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }
}
