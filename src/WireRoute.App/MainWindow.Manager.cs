using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WireRoute.App.Interop;
using WireRoute.App.Models;
using WireRoute.Core.Manager;
using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource managerCancellation = new();
    private ManagerCapabilities? managerCapabilities;

    private async Task InitializeManagerAsync()
    {
        if (managerClient is null)
        {
            if (managerLaunchError is not null)
            {
                await ShowMessageAsync("Tunnel manager is unavailable", managerLaunchError);
            }

            UpdateRouterOSManagerAvailability();
            return;
        }

        try
        {
            var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var hello = await managerClient.HelloAsync(
                version,
                architecture,
                managerCancellation.Token);
            managerCapabilities = hello.Capabilities;
            await RefreshManagerProfilesAsync();
            UpdateRouterOSManagerAvailability();
            _ = ReadManagerEventsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            managerCapabilities = null;
            UpdateRouterOSManagerAvailability();
            await ShowMessageAsync("Tunnel manager is unavailable", exception.Message);
        }
    }

    private async Task RefreshManagerProfilesAsync()
    {
        if (managerClient is null || managerCapabilities?.CanListProfiles != true)
        {
            return;
        }

        var response = await managerClient.RequestAsync<ManagerListProfilesRequest, ManagerListProfilesResponse>(
            ManagerMethods.ListProfiles,
            new ManagerListProfilesRequest(),
            managerCancellation.Token);
        var managerNames = response.Profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = Profiles.Count - 1; index >= 0; index--)
        {
            if (Profiles[index].ManagerName is not null && !managerNames.Contains(Profiles[index].ManagerName!))
            {
                Profiles.RemoveAt(index);
            }
        }

        foreach (var summary in response.Profiles)
        {
            var existing = Profiles.FirstOrDefault(item =>
                item.ManagerName?.Equals(summary.Name, StringComparison.OrdinalIgnoreCase) == true);
            if (existing is null)
            {
                Profiles.Add(new ProfileNavigationItem(summary));
            }
            else
            {
                existing.UpdateState(summary.State);
            }
        }
    }

    private async Task ReadManagerEventsAsync()
    {
        if (managerClient is null)
        {
            return;
        }

        try
        {
            while (!managerCancellation.IsCancellationRequested)
            {
                var managerEvent = await managerClient.ReadEventAsync(managerCancellation.Token);
                switch (managerEvent.Event)
                {
                    case ManagerEvents.ProfilesChanged:
                        await RefreshManagerProfilesAsync();
                        break;
                    case ManagerEvents.TunnelStateChanged:
                        var state = managerEvent.GetRequiredPayload<ManagerTunnelStateChangedEvent>();
                        UpdateManagerProfileState(state.Name, state.State);
                        break;
                    case ManagerEvents.ManagerStopping:
                        managerCapabilities = null;
                        UpdateRouterOSManagerAvailability();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            managerCapabilities = null;
            UpdateRouterOSManagerAvailability();
        }
    }

    private void UpdateManagerProfileState(string managerName, ManagerTunnelState state)
    {
        var item = Profiles.FirstOrDefault(value =>
            value.ManagerName?.Equals(managerName, StringComparison.OrdinalIgnoreCase) == true);
        if (item is null)
        {
            return;
        }

        item.UpdateState(state);
        var index = Profiles.IndexOf(item);
        Profiles.RemoveAt(index);
        Profiles.Insert(index, item);
        if (ReferenceEquals(selectedProfile, item))
        {
            ProfilesList.SelectedItem = item;
            SetProfileManagerControls(item);
        }
    }

    private void SetProfileManagerControls(ProfileNavigationItem item)
    {
        ToolTipService.SetToolTip(
            ProfileStorageStatusText,
            item.IsManaged
                ? "Saved securely by the WireRoute tunnel manager"
                : item.IsStoredLocally
                    ? "Saved securely on this PC with Windows DPAPI"
                    : "Imported locally for review");
        if (item.IsStoredLocally && !item.IsManaged)
        {
            item.UpdateState(localTunnelController.GetState(item.Name));
            ProfileStorageStatusText.Text = item.Status;
            ProfileConnectButton.IsEnabled = localTunnelController.IsAvailable
                && item.LocalTunnelState is LocalTunnelState.Inactive or LocalTunnelState.Active;
            ProfileConnectButton.Content = item.LocalTunnelState switch
            {
                LocalTunnelState.Active => "Deactivate",
                LocalTunnelState.Activating or LocalTunnelState.Deactivating => "Please wait…",
                _ => "Activate",
            };
            ToolTipService.SetToolTip(
                ProfileConnectButton,
                "Activate with WireGuardNT. Windows asks for approval only when starting or stopping this tunnel.");
            return;
        }

        ProfileStorageStatusText.Text = item.Status;

        var canChangeState = item.ManagerState == ManagerTunnelState.Started
            ? managerCapabilities?.CanStopTunnels == true
            : managerCapabilities?.CanStartTunnels == true;
        ProfileConnectButton.IsEnabled = item.IsManaged
            && managerClient is not null
            && canChangeState
            && item.ManagerState is ManagerTunnelState.Stopped or ManagerTunnelState.Started;
        ProfileConnectButton.Content = item.ManagerState == ManagerTunnelState.Started
            ? "Deactivate"
            : item.ManagerState is ManagerTunnelState.Starting or ManagerTunnelState.Stopping
                ? "Please wait…"
                : "Activate";
        ToolTipService.SetToolTip(
            ProfileConnectButton,
            item.IsManaged
                ? "Connect or disconnect this profile through the privileged WireRoute tunnel service."
                : "Import this profile into the manager before connecting.");
    }

    private async void ProfileConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var item = selectedProfile;
        if (item is null)
        {
            return;
        }

        ProfileConnectButton.IsEnabled = false;
        await ToggleProfileAsync(item);
    }

    private Task ToggleProfileAsync(ProfileNavigationItem item) =>
        item.IsStoredLocally && !item.IsManaged
            ? ToggleLocalProfileAsync(item)
            : ToggleManagerProfileAsync(item);

    private async Task ToggleLocalProfileAsync(ProfileNavigationItem item)
    {
        if (item.StoredProfile is null)
        {
            return;
        }

        var wasActive = localTunnelController.GetState(item.Name) == LocalTunnelState.Active;
        item.UpdateState(wasActive ? LocalTunnelState.Deactivating : LocalTunnelState.Activating);
        RefreshProfileListItem(item);
        try
        {
            if (wasActive)
            {
                await localTunnelController.StopAsync(item.Name, managerCancellation.Token);
            }
            else
            {
                await localTunnelController.StartAsync(item.StoredProfile, managerCancellation.Token);
            }
            item.UpdateState(localTunnelController.GetState(item.Name));
        }
        catch (OperationCanceledException exception)
        {
            item.UpdateState(localTunnelController.GetState(item.Name));
            RestoreWindowFromTray();
            await ShowMessageAsync("Tunnel approval was canceled", exception.Message);
        }
        catch (Exception exception)
        {
            item.UpdateState(localTunnelController.GetState(item.Name));
            RestoreWindowFromTray();
            await ShowMessageAsync("Tunnel state could not be changed", exception.Message);
        }
        finally
        {
            RefreshProfileListItem(item);
            if (ReferenceEquals(selectedProfile, item))
            {
                SetProfileManagerControls(item);
            }
        }
    }

    private void RefreshProfileListItem(ProfileNavigationItem item)
    {
        var index = Profiles.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        Profiles.RemoveAt(index);
        Profiles.Insert(index, item);
        if (ReferenceEquals(selectedProfile, item))
        {
            ProfilesList.SelectedItem = item;
        }
    }

    private async Task ToggleManagerProfileAsync(ProfileNavigationItem item)
    {
        if (item.ManagerName is null || managerClient is null)
        {
            return;
        }

        try
        {
            var method = item.ManagerState == ManagerTunnelState.Started
                ? ManagerMethods.StopTunnel
                : ManagerMethods.StartTunnel;
            var response = await managerClient.RequestAsync<ManagerTunnelCommandRequest, ManagerTunnelCommandResponse>(
                method,
                new ManagerTunnelCommandRequest(item.ManagerName),
                managerCancellation.Token);
            UpdateManagerProfileState(response.Name, response.State);
        }
        catch (Exception exception)
        {
            RestoreWindowFromTray();
            await ShowMessageAsync("Tunnel state could not be changed", exception.Message);
            if (ReferenceEquals(selectedProfile, item))
            {
                SetProfileManagerControls(item);
            }
        }
    }

    private async Task<ProfileNavigationItem> ImportGeneratedProfileAsync(
        string displayName,
        string wgQuickConfiguration)
    {
        if (managerClient is null || managerCapabilities?.CanImportProfiles != true)
        {
            var localProfile = WireGuardConfigParser.Parse(wgQuickConfiguration, displayName);
            var now = DateTimeOffset.UtcNow;
            var storedProfile = new WireRouteStoredProfile(
                Guid.NewGuid(),
                displayName,
                wgQuickConfiguration,
                localProfile.DetectedRouteMode == TunnelRouteMode.Full
                    ? StoredTunnelRouteMode.Full
                    : StoredTunnelRouteMode.Split,
                localProfile.SuggestedSplitAllowedIps.Select(route => route.Notation).ToArray(),
                StoredDnsProtectionMode.Profile,
                null,
                null,
                Array.Empty<string>(),
                OnDemandEthernet: false,
                OnDemandWiFi: false,
                now,
                now);
            await profileStore.SaveAsync(storedProfile, managerCancellation.Token);
            var localItem = new ProfileNavigationItem(storedProfile, localProfile);
            Profiles.Add(localItem);
            ProfilesList.SelectedItem = localItem;
            return localItem;
        }

        var result = await managerClient.RequestAsync<ManagerImportProfileRequest, ManagerImportProfileResponse>(
            ManagerMethods.ImportProfile,
            new ManagerImportProfileRequest(displayName, wgQuickConfiguration),
            managerCancellation.Token);
        var profile = WireGuardConfigParser.Parse(wgQuickConfiguration, result.Profile.Name);
        var item = new ProfileNavigationItem(
            profile,
            result.Profile.Name,
            result.Profile.DisplayName,
            result.Profile.State);
        var existing = Profiles.FirstOrDefault(value =>
            value.ManagerName?.Equals(result.Profile.Name, StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null)
        {
            Profiles.Remove(existing);
        }

        Profiles.Add(item);
        ProfilesList.SelectedItem = item;
        return item;
    }

    private void UpdateRouterOSManagerAvailability()
    {
        var hasDiscovery = routerOSConnectedContext is not null;
        var hasInterfaces = routerOSInterfaces.Count > 0;
        var canImport = true;
        RouterOSSetUpPeerButton.IsEnabled = hasDiscovery
            && hasInterfaces
            && !isRouterOSBusy
            && canImport;
        RouterOSPeerActionHelpText.Text = !hasDiscovery
            ? "Connect to this router to enable peer setup."
            : !hasInterfaces
                ? "No WireGuard interfaces were found on this router."
                : string.Empty;
        RouterOSPeerActionHelpText.Visibility = RouterOSSetUpPeerButton.IsEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        trayIcon.Dispose();
        managerCancellation.Cancel();
        if (managerClient is not null)
        {
            await managerClient.DisposeAsync();
        }

        managerCancellation.Dispose();
    }
}
