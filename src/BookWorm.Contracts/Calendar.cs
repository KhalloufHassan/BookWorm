namespace BookWorm.Contracts;

/// <summary>
/// A finished read for the reading calendar. Dates are in the time zone the app asked for.
/// </summary>
/// <param name="Started">When the read started; null when only the finish date is known.</param>
/// <param name="CoverVersion">See <see cref="BookSummary.CoverVersion"/>.</param>
public sealed record CalendarRead(
    Guid ReadId,
    Guid BookId,
    string Title,
    IReadOnlyList<AuthorRef> Authors,
    long? CoverVersion,
    decimal? Rating,
    DateOnly? Started,
    DateOnly Finished);
