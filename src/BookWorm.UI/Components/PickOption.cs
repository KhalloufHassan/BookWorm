namespace BookWorm.UI.Components;

/// <summary>An existing author or tag, or (without an id) one that would be created.</summary>
public sealed record PickOption(Guid? Id, string Name)
{
    public bool IsNew => Id is null;
}
