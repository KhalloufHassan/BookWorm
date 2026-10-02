using Microsoft.Maui.Networking;

namespace BookWorm.Mobile.Services;

/// <summary>The phone's internet connection, from MAUI.</summary>
public sealed class DeviceConnectivity : Core.Offline.IConnectivity
{
    public DeviceConnectivity() => Connectivity.Current.ConnectivityChanged += (_, _) => Changed?.Invoke();

    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public event Action Changed;
}
