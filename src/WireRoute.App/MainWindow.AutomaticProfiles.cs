using Microsoft.UI.Dispatching;
using WireRoute.App.Interop;
using WireRoute.App.Models;
using WireRoute.Core.Profiles;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private readonly AutomaticProfileSession automaticSession = new();
    private DispatcherQueueTimer? automaticTimer;
    private bool automaticSettingsLoaded;
    private bool automaticProfilesLoaded;
    private string automaticStatus = "Automatic profiles are off.";
    private string? pendingNetworkIdentity;
    private DateTimeOffset networkStableSince;

    private void StartAutomaticProfilePolling()
    {
        automaticTimer = DispatcherQueue.CreateTimer();
        automaticTimer.Interval = TimeSpan.FromSeconds(2);
        automaticTimer.Tick += async (_, _) =>
        {
            if (appSettings.AutomaticProfiles.Enabled) await EvaluateOnDemandAsync();
        };
        automaticTimer.Start();
    }

    private ProfileNetworkSnapshot ReadAutomaticNetwork() => ProfileNetworkMonitor.Read(
        Profiles.Where(p => p.StoredProfile is not null && !p.IsManaged)
            .Select(p => p.StoredProfile!.TunnelName).ToHashSet(StringComparer.OrdinalIgnoreCase),
        appSettings.AutomaticProfiles.NeedsWiFiNames);

    private void PauseAutomaticProfilesForManualControl()
    {
        if (!appSettings.AutomaticProfiles.Enabled) return;
        try { automaticSession.ObserveNetwork(ReadAutomaticNetwork().Identity); }
        catch { /* Manual control must remain available if network discovery fails. */ }
        automaticSession.ManualControl();
        UpdateAutomaticProfilesStatus("Paused after manual control. Resumes when the network changes or rules are saved.");
    }

    private void UpdateAutomaticProfilesStatus(string? message = null)
    {
        if (message is not null) automaticStatus = message;
        AutomaticProfilesStatusText.Text = appSettings.AutomaticProfiles.Enabled ? automaticStatus
            : appSettings.SingleProfileOnDemandSuspended
                ? "Off. Saved single-profile On-Demand rules remain paused."
                : "Off. Choose profiles for Wi-Fi, cellular, and Ethernet networks.";
        UpdateProfileOnDemandSummary(selectedProfile);
    }

    private HashSet<Guid> ActiveLocalProfileIds() => Profiles
        .Where(p => p.StoredProfile is not null && !p.IsManaged
            && localTunnelController.GetState(p.StoredProfile.TunnelName) != LocalTunnelState.Inactive)
        .Select(p => p.StoredProfile!.Id).ToHashSet();

    private bool CanApplyAutomaticSwitch(ProfileSwitchStamp stamp, Guid id, bool connecting)
    {
        var snapshot = ReadAutomaticNetwork();
        automaticSession.ObserveNetwork(snapshot.Identity);
        return !isExiting && activeModal is null && automaticSession.Permits(stamp, appSettings.AutomaticProfiles.Enabled,
            ActiveLocalProfileIds(), snapshot.AnotherVpn || Profiles.Any(p => p.IsManaged && (p.IsActive || p.IsTransitioning)),
            id, connecting);
    }

    private async Task EvaluateAutomaticProfilesAsync()
    {
        if (!await onDemandGate.WaitAsync(0)) return;
        try
        {
            var policy = appSettings.AutomaticProfiles;
            if (!policy.Enabled) return;
            var snapshot = ReadAutomaticNetwork();
            automaticSession.ObserveNetwork(snapshot.Identity);
            // Events and a low-frequency retry cooperate: act only after a stable
            // physical network, not during DHCP, resume, or a burst of VPN events.
            if (pendingNetworkIdentity != snapshot.Identity)
            {
                pendingNetworkIdentity = snapshot.Identity;
                networkStableSince = DateTimeOffset.UtcNow;
            }
            if (DateTimeOffset.UtcNow - networkStableSince < TimeSpan.FromSeconds(2)) return;
            if (automaticSession.IsPaused)
            {
                // Preserve the reason (especially an elevation failure) in Settings.
                return;
            }
            if (activeModal is not null || Profiles.Any(p => p.IsTransitioning)) return;
            var active = ActiveLocalProfileIds();
            if (snapshot.AnotherVpn || Profiles.Any(p => p.IsManaged && (p.IsActive || p.IsTransitioning))
                || active.Any(id => id != automaticSession.OwnedProfileId))
            {
                UpdateAutomaticProfilesStatus("A manual VPN or another virtual network is active. Automatic profiles will not replace it.");
                return;
            }
            if (automaticSession.OwnedProfileId is not null && active.Count == 0)
            {
                // An external stop is also manual intent; do not fight another controller.
                automaticSession.ManualControl();
                UpdateAutomaticProfilesStatus("The automatic tunnel was stopped outside this operation. Paused for this network.");
                return;
            }
            var candidates = Profiles.Where(p => p.StoredProfile is not null && !p.IsManaged)
                .ToDictionary(p => p.StoredProfile!.Id);
            var decision = policy.Decide(snapshot.Transport, snapshot.Ssid, candidates.Keys.ToHashSet());
            if (decision.HoldReason is not null)
            {
                UpdateAutomaticProfilesStatus(decision.HoldReason);
                return;
            }
            var desired = decision.ProfileId is Guid target ? candidates[target] : null;
            // Validate before disconnecting the old VPN. No config hooks or
            // unsupported persistent/encrypted DNS combinations may reach handover.
            if (desired is not null)
            {
                if (!localTunnelController.IsAvailable)
                    throw new InvalidOperationException("The native tunnel backend is unavailable.");
                var parsed = WireGuardConfigParser.Parse(desired.StoredProfile!.Configuration, desired.StoredProfile.TunnelName);
                if (parsed.Interface.HasHooks)
                    throw new InvalidOperationException("The assigned profile contains blocked script hooks. Edit it before switching.");
                if (appSettings.PersistentTunnelService && desired.StoredProfile.DnsProtectionMode == StoredDnsProtectionMode.Encrypted)
                    throw new InvalidOperationException("The assigned profile uses encrypted DNS, which cannot run as a persistent tunnel.");
                if (desired.StoredProfile.DnsProtectionMode == StoredDnsProtectionMode.Encrypted
                    && (!Uri.TryCreate(desired.StoredProfile.DnsResolverUrl, UriKind.Absolute, out var resolver)
                        || resolver.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException("The assigned profile does not contain a valid HTTPS DNS resolver.");
            }
            var stamp = automaticSession.Stamp;
            var previous = automaticSession.OwnedProfileId is Guid owned ? candidates.GetValueOrDefault(owned) : null;
            if (previous is not null && previous != desired)
            {
                if (!CanApplyAutomaticSwitch(stamp, previous.StoredProfile!.Id, connecting: false)) return;
                UpdateAutomaticProfilesStatus("Switching profiles. Windows may ask for approval to disconnect…");
                await ChangeLocalProfileStateAsync(previous, automatic: true);
                if (localTunnelController.GetState(previous.StoredProfile.TunnelName) != LocalTunnelState.Inactive)
                    throw new InvalidOperationException("The previous tunnel did not stop. No replacement was started.");
                automaticSession.Disconnected();
            }
            if (desired is not null && localTunnelController.GetState(desired.StoredProfile!.TunnelName) != LocalTunnelState.Active)
            {
                if (!CanApplyAutomaticSwitch(stamp, desired.StoredProfile.Id, connecting: true)) return;
                UpdateAutomaticProfilesStatus($"Connecting {desired.Name}. Windows may ask for approval…");
                await ChangeLocalProfileStateAsync(desired, automatic: true);
                if (localTunnelController.GetState(desired.StoredProfile.TunnelName) != LocalTunnelState.Active)
                    throw new InvalidOperationException("The assigned tunnel did not become active.");
                automaticSession.Connected(desired.StoredProfile.Id, stamp.Revision, appSettings.AutomaticProfiles.Enabled);
            }
            if (stamp != automaticSession.Stamp) return;
            UpdateAutomaticProfilesStatus(desired is null ? "VPN off for this network. Watching for network changes."
                : $"{desired.Name} connected automatically.");
        }
        catch (Exception exception)
        {
            // Do not reopen dialogs or repeatedly request elevation after a denial.
            automaticSession.PauseAfterFailure();
            var message = "Automatic profiles paused: " + exception.Message;
            var changed = automaticStatus != message;
            UpdateAutomaticProfilesStatus(message);
            if (changed)
                await RecordActivityAsync(WireRouteActivityKind.TunnelError, null,
                    "Automatic profile switching paused: " + exception.Message);
        }
        finally { onDemandGate.Release(); }
    }
}
