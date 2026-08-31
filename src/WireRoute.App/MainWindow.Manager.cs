using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WireRoute.App.Models;
using WireRoute.Core.Manager;
using WireRoute.Core.Profiles;

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
        ProfileStorageStatusText.Text = item.IsManaged
            ? "Saved securely by the WireRoute tunnel manager"
            : "Imported locally for review • Not saved to the manager";
        var canChangeState = item.ManagerState == ManagerTunnelState.Started
            ? managerCapabilities?.CanStopTunnels == true
            : managerCapabilities?.CanStartTunnels == true;
        ProfileConnectButton.IsEnabled = item.IsManaged
            && managerClient is not null
            && canChangeState
            && item.ManagerState is ManagerTunnelState.Stopped or ManagerTunnelState.Started;
        ProfileConnectButton.Content = item.ManagerState == ManagerTunnelState.Started
            ? "Disconnect"
            : item.ManagerState is ManagerTunnelState.Starting or ManagerTunnelState.Stopping
                ? "Please wait…"
                : "Connect";
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
        await ToggleManagerProfileAsync(item);
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
            throw new ManagerProtocolException(
                "The privileged tunnel manager is not connected. Restart WireRoute through its installed service and try again.");
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
        RouterOSSetUpPeerButton.IsEnabled = routerOSConnectedContext is not null
            && routerOSInterfaces.Count > 0
            && !isRouterOSBusy
            && managerCapabilities?.CanImportProfiles == true;
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
