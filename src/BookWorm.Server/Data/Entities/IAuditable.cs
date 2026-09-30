namespace BookWorm.Server.Data;

/// <summary>Rows that record when they were created and last changed (UTC). Set automatically on save.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
