namespace BookWorm.Contracts;

/// <summary>Where a book stands for its owner. Always set by hand.</summary>
public enum BookStatus
{
    WantToRead,
    CurrentlyReading,
    Finished,
    Skipped,
}

/// <summary>How a single read-through of a book went.</summary>
public enum ReadStatus
{
    CurrentlyReading,
    Finished,
    Skipped,
}
