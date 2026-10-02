namespace BookWorm.Mobile.Core.Offline;

/// <summary>Whether the phone has an internet connection (the server may still be unreachable).</summary>
public interface IConnectivity
{
    bool IsOnline { get; }

    event Action Changed;
}
