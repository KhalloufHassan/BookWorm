namespace BookWorm.Server.Data;

/// <summary>Join row between a collection and one of its books.</summary>
public sealed class CollectionBook
{
    public Guid CollectionId { get; set; }
    public Guid BookId { get; set; }

    /// <summary>Order within the collection, from 0.</summary>
    public int Position { get; set; }

    public Collection Collection { get; set; }
    public Book Book { get; set; }
}
