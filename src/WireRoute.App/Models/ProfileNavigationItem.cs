using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using WireRoute.Core.Profiles;
using WireRoute.Core.Manager;
using WireRoute.Core.Routing;
using WireRoute.RouterOS;
using WireRoute.Storage;
using WireRoute.App.Interop;

namespace WireRoute.App.Models;

public sealed class ProfileNavigationItem : INotifyPropertyChanged
{
    private string name = string.Empty;
    private string status = string.Empty;
    private string routingLabel = string.Empty;

    public ProfileNavigationItem(WireRouteStoredProfile storedProfile, WireGuardProfile profile)
        : this(profile, displayName: storedProfile.Name)
    {
        StoredProfile = storedProfile;
        InterfacePublicKey = TryInterfacePublicKey(profile);
        Status = "Inactive";
        RoutingLabel = storedProfile.RouteMode == StoredTunnelRouteMode.Full ? "Full" : "Split";
    }

    public ProfileNavigationItem(
        WireGuardProfile profile,
        string? managerName = null,
        string? displayName = null,
        ManagerTunnelState managerState = ManagerTunnelState.Unknown)
    {
        Profile = profile;
        InterfacePublicKey = TryInterfacePublicKey(profile);
        ManagerName = managerName;
        Name = displayName ?? profile.Name;
        ManagerState = managerState;
        Status = managerName is null ? "Imported preview" : StatusText(managerState);
        RoutingLabel = profile.DetectedRouteMode == TunnelRouteMode.Full ? "Full" : "Split";
    }

    public ProfileNavigationItem(ManagerProfileSummary profile)
    {
        ManagerName = profile.Name;
        InterfacePublicKey = profile.InterfacePublicKey;
        Name = profile.DisplayName;
        ManagerState = profile.State;
        Status = StatusText(profile.State);
        RoutingLabel = profile.DetectedRouteMode == TunnelRouteMode.Full ? "Full" : "Split";
    }

    public WireGuardProfile? Profile { get; private set; }

    public WireRouteStoredProfile? StoredProfile { get; private set; }

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public string Status
    {
        get => status;
        set => SetField(ref status, value);
    }

    public string RoutingLabel
    {
        get => routingLabel;
        set => SetField(ref routingLabel, value);
    }

    public string? InterfacePublicKey { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal string? ManagerName { get; }

    internal ManagerTunnelState ManagerState { get; private set; }

    internal bool IsManaged => ManagerName is not null;

    internal bool IsStoredLocally => StoredProfile is not null;

    internal LocalTunnelState LocalTunnelState { get; private set; } = LocalTunnelState.Inactive;

    internal bool IsActive => IsManaged
        ? ManagerState == ManagerTunnelState.Started
        : LocalTunnelState == LocalTunnelState.Active;

    internal bool IsTransitioning => IsManaged
        ? ManagerState is ManagerTunnelState.Starting or ManagerTunnelState.Stopping
        : LocalTunnelState is LocalTunnelState.Activating or LocalTunnelState.Deactivating;

    internal void UpdateStoredProfile(WireRouteStoredProfile storedProfile, WireGuardProfile profile)
    {
        StoredProfile = storedProfile;
        Profile = profile;
        InterfacePublicKey = TryInterfacePublicKey(profile);
        Name = storedProfile.Name;
        RoutingLabel = storedProfile.RouteMode == StoredTunnelRouteMode.Full ? "Full" : "Split";
    }

    internal void UpdateState(ManagerTunnelState state)
    {
        ManagerState = state;
        Status = StatusText(state);
    }

    internal void UpdateSummary(ManagerProfileSummary profile)
    {
        InterfacePublicKey = profile.InterfacePublicKey;
        Name = profile.DisplayName;
        RoutingLabel = profile.DetectedRouteMode == TunnelRouteMode.Full ? "Full" : "Split";
        UpdateState(profile.State);
    }

    internal void UpdateState(LocalTunnelState state)
    {
        LocalTunnelState = state;
        Status = state switch
        {
            LocalTunnelState.Activating => "Activating",
            LocalTunnelState.Active => "Active",
            LocalTunnelState.Deactivating => "Deactivating",
            LocalTunnelState.Inactive => "Inactive",
            _ => "Status unavailable",
        };
    }

    private static string StatusText(ManagerTunnelState state) => state switch
    {
        ManagerTunnelState.Starting => "Connecting",
        ManagerTunnelState.Started => "Connected",
        ManagerTunnelState.Stopping => "Disconnecting",
        ManagerTunnelState.Stopped => "Disconnected",
        _ => "Status unavailable",
    };

    private static string? TryInterfacePublicKey(WireGuardProfile profile)
    {
        try
        {
            return WireGuardKeyPair.FromPrivateKey(
                WireGuardConfigFormatter.PrivateKey(profile)).PublicKey;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private void SetField(
        ref string field,
        string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (field.Equals(value, StringComparison.Ordinal))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
