using Microsoft.UI.Xaml.Controls;
using WireRoute.App.Models;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private async Task<bool> ConfirmPersistentTunnelModeChangeAsync(bool enable)
    {
        var activeLocalProfiles = Profiles.Count(profile =>
            profile.StoredProfile is not null
            && !profile.IsManaged
            && localTunnelController.GetState(profile.StoredProfile.TunnelName)
                == Interop.LocalTunnelState.Active);
        var persistentProfiles = Profiles.Count(profile =>
            profile.StoredProfile is not null
            && localTunnelController.IsPersistentService(profile.StoredProfile.TunnelName));
        var message = enable
            ? activeLocalProfiles > 0
                ? "WireRoute will reconnect the active tunnel using an automatic per-tunnel Windows service. Windows will ask for approval while the service is replaced. The tunnel can then remain connected across sign-out and restart."
                : "The next profile you activate will install an automatic per-tunnel Windows service and can remain connected across sign-out and restart. The always-on WireGuard manager service will not be installed."
            : persistentProfiles > 0
                ? "WireRoute will disconnect and remove its persistent tunnel service. Your protected local profiles remain available, and future connections return to demand-start service-free operation from the notification area."
                : "Future connections will use demand-start service-free operation from the notification area. Your protected local profiles are not changed.";

        var result = await ShowModalAsync(new ModalRequest
        {
            Title = enable ? "Enable Persistent VPN?" : "Disable Persistent VPN?",
            IconGlyph = "\uE83D",
            Content = new TextBlock
            {
                MaxWidth = 610,
                Text = message,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            },
            PrimaryText = enable ? "Enable Persistent VPN" : "Disable Persistent VPN",
            CancelText = "Cancel",
            MaxWidth = 680,
        });
        return result == WireRouteModalResult.Primary;
    }

    private async Task ApplyPersistentTunnelModeAsync(
        bool enable,
        CancellationToken cancellationToken)
    {
        if (managerClient is not null)
        {
            throw new InvalidOperationException(
                "This WireRoute window is controlled by the legacy manager service. Disable that service before changing service-free persistence.");
        }

        var localProfiles = Profiles
            .Where(profile => profile.StoredProfile is not null && !profile.IsManaged)
            .ToArray();
        if (!enable)
        {
            foreach (var item in localProfiles)
            {
                var profile = item.StoredProfile!;
                if (!localTunnelController.IsPersistentService(profile.TunnelName))
                {
                    continue;
                }

                await localTunnelController.StopAsync(profile, cancellationToken);
                item.UpdateState(Interop.LocalTunnelState.Inactive);
                RefreshProfileListItem(item);
                await RecordActivityAsync(
                    WireRouteActivityKind.ProfileDeactivated,
                    item,
                    "Removed the persistent Windows tunnel service and disconnected the tunnel.");
            }
            return;
        }

        var activeProfiles = localProfiles
            .Where(item => localTunnelController.GetState(item.StoredProfile!.TunnelName)
                == Interop.LocalTunnelState.Active)
            .Where(item => !localTunnelController.IsPersistentService(
                item.StoredProfile!.TunnelName))
            .ToArray();
        var encryptedDnsProfile = activeProfiles.FirstOrDefault(item =>
            item.StoredProfile!.DnsProtectionMode == StoredDnsProtectionMode.Encrypted);
        if (encryptedDnsProfile is not null)
        {
            throw new InvalidOperationException(
                "“" + encryptedDnsProfile.Name + "” uses WireRoute's in-process encrypted DNS proxy. Choose Profile DNS before making this tunnel persistent.");
        }

        foreach (var item in activeProfiles)
        {
            var profile = item.StoredProfile!;
            item.UpdateState(Interop.LocalTunnelState.Deactivating);
            RefreshProfileListItem(item);
            await localTunnelController.StopAsync(profile, cancellationToken);
            try
            {
                item.UpdateState(Interop.LocalTunnelState.Activating);
                RefreshProfileListItem(item);
                await localTunnelController.StartAsync(
                    profile,
                    persistent: true,
                    cancellationToken: cancellationToken);
            }
            catch
            {
                try
                {
                    await localTunnelController.StartAsync(
                        profile,
                        persistent: false,
                        cancellationToken: cancellationToken);
                }
                catch
                {
                    // The original exception describes why persistence failed.
                }
                item.UpdateState(localTunnelController.GetState(profile.TunnelName));
                RefreshProfileListItem(item);
                throw;
            }

            item.UpdateState(localTunnelController.GetState(profile.TunnelName));
            RefreshProfileListItem(item);
            await RecordActivityAsync(
                WireRouteActivityKind.ProfileActivated,
                item,
                "Upgraded the active tunnel to an automatic persistent Windows service.");
        }
    }

    private void UpdatePersistentTunnelStatusText(bool enabled)
    {
        var persistentCount = Profiles.Count(profile =>
            profile.StoredProfile is not null
            && localTunnelController.IsPersistentService(profile.StoredProfile.TunnelName));
        SettingsPersistentServiceStatusText.Text = enabled
            ? persistentCount > 0
                ? persistentCount == 1
                    ? "1 persistent tunnel service is installed."
                    : $"{persistentCount} persistent tunnel services are installed."
                : "No service is installed yet. The next tunnel you activate will set one up."
            : persistentCount > 0
                ? "A persistent tunnel service is still present; save this setting to remove it."
                : "Tunnels run on demand and are removed when disconnected.";
    }
}
