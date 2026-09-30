using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using BookWorm.Contracts;
using BookWorm.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace BookWorm.Server.Api;

/// <summary>Validates a request body with its data annotations before the handler runs.</summary>
internal sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<T>().FirstOrDefault();
        if (request is null)
        {
            return TypedResults.Problem("A request body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return TypedResults.ValidationProblem(ApiErrors.ToDictionary(results));
        }

        return await next(context);
    }
}

internal static class ApiErrors
{
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();

    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [FieldName(field)] = [message] });

    public static Conflict<ProblemDetails> Conflict(string detail) =>
        TypedResults.Conflict(new ProblemDetails
        {
            Title = "Conflict",
            Detail = detail,
            Status = StatusCodes.Status409Conflict,
        });

    public static Conflict<ProblemDetails> ChangedElsewhere(string what) =>
        Conflict($"This {what} was changed somewhere else since you loaded it. Reload it and try again.");

    public static Dictionary<string, string[]> ToDictionary(IEnumerable<ValidationResult> results) =>
        results
            .SelectMany(result => (result.MemberNames.Any() ? result.MemberNames : [""])
                .Select(member => (Field: FieldName(member), Message: result.ErrorMessage ?? "The value is not valid.")))
            .GroupBy(error => error.Field, error => error.Message)
            .ToDictionary(group => group.Key, group => group.ToArray());

    public static bool IsUniqueViolation(Exception exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string FieldName(string member) => JsonNamingPolicy.CamelCase.ConvertName(member);
}

internal static class Paging
{
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? ApiLimits.DefaultPageSize, 1, ApiLimits.MaxPageSize));

    public static IQueryable<T> Page<T>(this IQueryable<T> query, int page, int pageSize) =>
        query.Skip((page - 1) * pageSize).Take(pageSize);
}

/// <summary>Splits search input into words and builds the LIKE patterns used to match them.</summary>
internal static class SearchText
{
    /// <summary>
    /// pg_trgm strict word similarity needed for an approximate match. About 0.4 accepts typical typos
    /// ("tolkin", "garsia", "shakespear") while rejecting look-alike words ("empire" vs "vampire").
    /// </summary>
    public const double FuzzyThreshold = 0.39;

    /// <summary>Words with fewer letters than this (including numbers like "1984") only match exactly.</summary>
    private const int FuzzyMinLetters = 4;

    public const string LikeEscape = @"\";

    private const int MaxTerms = 8;

    public static string[] Terms(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [];
        }

        var trimmed = search.Trim();
        if (trimmed.Length > ApiLimits.MaxSearchLength)
        {
            trimmed = trimmed[..ApiLimits.MaxSearchLength];
        }

        return trimmed
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTerms)
            .ToArray();
    }

    public static bool AllowsFuzzyMatch(string term) => term.Count(char.IsLetter) >= FuzzyMinLetters;

    public static string ContainsPattern(string term) => $"%{EscapeLike(term)}%";

    public static string ExactPattern(string text) => EscapeLike(text);

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}

internal static class ExpressionExtensions
{
    public static Expression<Func<T, bool>> OrElse<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ReplaceParameter(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left.Body, rightBody), parameter);
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

internal static class CurrentUserExtensions
{
    /// <summary>For endpoints that require authorization, where a signed-in user is guaranteed.</summary>
    public static Guid RequireUserId(this ICurrentUser currentUser) =>
        currentUser.UserId ?? throw new InvalidOperationException("This endpoint requires a signed-in user.");
}

internal static class ServerVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(ServerVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // Drop the "+<commit>" build metadata the SDK appends.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
