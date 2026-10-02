using BookWorm.Contracts;

namespace BookWorm.Mobile.Core.Offline;

/// <summary>
/// The downloaded file of a book (<c>file.json</c>). Written last, so a book only counts as
/// downloaded once everything else is in place. Its browse position is kept here too.
/// </summary>
public sealed record DownloadRecord(BookFileDetails File, DateTimeOffset DownloadedAt);
