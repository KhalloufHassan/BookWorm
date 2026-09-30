namespace BookWorm.Server.Data;

/// <summary>Join row between a book and a tag.</summary>
public sealed class BookTag
{
    public Guid BookId { get; set; }
    public Guid TagId { get; set; }

    public Book Book { get; set; }
    public Tag Tag { get; set; }
}
