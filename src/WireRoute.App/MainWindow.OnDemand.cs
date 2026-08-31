using Windows.Networking.Connectivity;
using WireRoute.App.Interop;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim onDemandGate = new(1, 1);
    private DateTimeOffset nextOnDemandAttempt = DateTimeOffset.MinValue;
    private bool isMonitoringNetworks;

    private void StartOnDemandMonitoring()
    {
        if (isMonitoringNetworks)
        {
            return;
        }
        NetworkInformation.NetworkStatusChanged += NetworkStatusChanged;
        isMonitoringNetworks = true;
    }

    private void StopOnDemandMonitoring()
    {
        if (!isMonitoringNetworks)
        {
            return;
        }
        NetworkInformation.NetworkStatusChanged -= NetworkStatusChanged;
        isMonitoringNetworks = false;
    }

    private void NetworkStatusChanged(object sender)
    {
        _ = DispatcherQueue.TryEnqueue(() => _ = EvaluateOnDemandAsync());
    }

    private async Task EvaluateOnDemandAsync()
    {
        if (DateTimeOffset.UtcNow < nextOnDemandAttempt
            || Profiles.Any(profile => profile.IsActive || profile.IsTransitioning)
            || !await onDemandGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var connections = NetworkInformation.GetConnectionProfiles()
                .Where(profile =>
                    profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                .ToArray();
            var hasWiFi = connections.Any(profile =>
                profile.IsWlanConnectionProfile
                || profile.NetworkAdapter?.IanaInterfaceType == 71);
            var hasEthernet = connections.Any(profile =>
                profile.NetworkAdapter?.IanaInterfaceType == 6);
            var candidate = Profiles.FirstOrDefault(profile =>
                profile.StoredProfile is not null
                && !profile.IsManaged
                && localTunnelController.GetState(profile.Name) == LocalTunnelState.Inactive
                && (profile.StoredProfile.OnDemandWiFi && hasWiFi
                    || profile.StoredProfile.OnDemandEthernet && hasEthernet));
            if (candidate is null)
            {
                return;
            }

            nextOnDemandAttempt = DateTimeOffset.UtcNow.AddMinutes(1);
            await RecordActivityAsync(
                WireRouteActivityKind.OnDemandMatched,
                candidate,
                "On-Demand matched the current Windows network.");
            await ToggleLocalProfileAsync(candidate);
        }
        catch (Exception exception)
        {
            await RecordActivityAsync(
                WireRouteActivityKind.TunnelError,
                null,
                "On-Demand evaluation failed: " + exception.Message);
        }
        finally
        {
            onDemandGate.Release();
        }
    }
}
