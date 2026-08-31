using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WireRoute.App.Interop;
using WireRoute.App.Models;
using WireRoute.Core.Manager;
using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;
using WireRoute.RouterOS;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow appWindow;
    private readonly nint windowHandle;
    private readonly TrayIcon trayIcon;
    private readonly ManagerProtocolClient? managerClient;
    private readonly string? managerLaunchError;
    private readonly WireGuardProfileStore profileStore = new();
    private readonly WireRouteActivityStore activityStore = new();
    private readonly TunnelServiceController localTunnelController = new();
    private ProfileNavigationItem? selectedProfile;
    private bool isExiting;

    public MainWindow(ManagerProtocolClient? managerClient = null, string? managerLaunchError = null)
    {
        this.managerClient = managerClient;
        this.managerLaunchError = managerLaunchError;
        InitializeComponent();
        Root.ActualThemeChanged += Root_ActualThemeChanged;

        windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        appWindow = AppWindow.GetFromWindowId(windowId);
        ConfigureWindow();
        trayIcon = new TrayIcon(
            windowHandle,
            AppIconPath(),
            RestoreWindowFromTray,
            CreateTrayMenuSnapshot,
            ExecuteTrayMenuAction);
        Profiles.CollectionChanged += (_, _) =>
        {
            UpdateProfilesEmptyState();
            UpdateTrayIconAppearance();
        };
        UpdateTrayIconAppearance();
        StartOnDemandMonitoring();
        StartActivityMonitoring();
        appWindow.Closing += AppWindow_Closing;
        UpdateProfilesEmptyState();
        _ = LoadStoredProfilesAsync();
        _ = LoadSettingsAsync();
        _ = LoadRouterOSConnectionsAsync();
        _ = InitializeManagerAsync();
        _ = RecordActivityAsync(
            WireRouteActivityKind.AppStarted,
            null,
            "WireRoute started in service-free mode.");
        Closed += MainWindow_Closed;
    }

    public ObservableCollection<ProfileNavigationItem> Profiles { get; } = [];

    private void ConfigureWindow()
    {
        appWindow.SetIcon(AppIconPath());
        appWindow.Resize(new SizeInt32(1180, 760));
        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 243, 247, 252);
            titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 127, 146, 168);
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 33, 50, 72);
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 53, 74, 98);
        }
    }

    private static string AppIconPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "wireroute.ico");

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (isExiting)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private void RestoreWindowFromTray()
    {
        appWindow.Show();
        Activate();
    }

    internal void RestoreFromExternalActivation() => RestoreWindowFromTray();

    private void ShowManageTunnels()
    {
        var preferredProfile = selectedProfile is not null && Profiles.Contains(selectedProfile)
            ? selectedProfile
            : Profiles.FirstOrDefault();
        if (preferredProfile is null)
        {
            ShowDestination(Destination.Profile);
        }
        else
        {
            ProfilesList.SelectedItem = preferredProfile;
        }

        ProfilesList.Focus(FocusState.Programmatic);
    }

    private void UpdateProfilesEmptyState()
    {
        ProfilesEmptyState.Visibility = Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadStoredProfilesAsync()
    {
        try
        {
            var storedProfiles = await profileStore.LoadAllAsync();
            foreach (var storedProfile in storedProfiles)
            {
                try
                {
                    var profile = WireGuardConfigParser.Parse(storedProfile.Configuration, storedProfile.Name);
                    var item = new ProfileNavigationItem(storedProfile, profile);
                    item.UpdateState(localTunnelController.GetState(item.Name));
                    Profiles.Add(item);
                    if (item.IsActive
                        && storedProfile.DnsProtectionMode == StoredDnsProtectionMode.Encrypted)
                    {
                        try
                        {
                            await localTunnelController.RestoreEncryptedDnsAsync(storedProfile);
                        }
                        catch (Exception exception)
                        {
                            await RecordActivityAsync(
                                WireRouteActivityKind.TunnelError,
                                item,
                                "Encrypted DNS could not be restored: " + exception.Message);
                        }
                    }
                }
                catch (WireGuardConfigParseException)
                {
                    // Leave malformed protected entries untouched for a future recovery flow.
                }
            }
            await EvaluateOnDemandAsync();

            if (ProfilesList.SelectedItem is null && Profiles.Count > 0)
            {
                ProfilesList.SelectedItem = Profiles[0];
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Saved profiles are unavailable", exception.Message);
        }
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await ImportProfilesAsync();
    }

    private async Task ImportProfilesAsync()
    {
        ImportProfileButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker
            {
                CommitButtonText = "Import",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".conf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0)
            {
                return;
            }

            var failures = new List<string>();
            ProfileNavigationItem? firstImportedItem = null;
            foreach (var file in files)
            {
                var profileName = Path.GetFileNameWithoutExtension(file.Name).Trim();
                if (Profiles.Any(item => item.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"{file.Name}: A profile with this name is already open.");
                    continue;
                }

                try
                {
                    var properties = await file.GetBasicPropertiesAsync();
                    if (properties.Size > 1024 * 1024)
                    {
                        failures.Add($"{file.Name}: Configuration files must be 1 MB or smaller.");
                        continue;
                    }

                    var text = await FileIO.ReadTextAsync(file);
                    var profile = WireGuardConfigParser.Parse(text, profileName);
                    var now = DateTimeOffset.UtcNow;
                    var storedProfile = new WireRouteStoredProfile(
                        Guid.NewGuid(),
                        profileName,
                        text,
                        profile.DetectedRouteMode == TunnelRouteMode.Full
                            ? StoredTunnelRouteMode.Full
                            : StoredTunnelRouteMode.Split,
                        profile.SuggestedSplitAllowedIps.Select(route => route.Notation).ToArray(),
                        StoredDnsProtectionMode.Profile,
                        null,
                        null,
                        Array.Empty<string>(),
                        OnDemandEthernet: false,
                        OnDemandWiFi: false,
                        now,
                        now);
                    await profileStore.SaveAsync(storedProfile);
                    var item = new ProfileNavigationItem(storedProfile, profile);
                    Profiles.Add(item);
                    await RecordActivityAsync(
                        WireRouteActivityKind.ProfileImported,
                        item,
                        "Imported " + file.Name + ".");
                    firstImportedItem ??= item;
                }
                catch (WireGuardConfigParseException exception)
                {
                    failures.Add($"{file.Name}: {exception.Message}");
                }
                catch (Exception exception)
                {
                    failures.Add($"{file.Name}: {exception.Message}");
                }
            }

            if (firstImportedItem is not null)
            {
                ProfilesList.SelectedItem = firstImportedItem;
            }

            if (failures.Count > 0)
            {
                var title = firstImportedItem is null ? "Profiles were not imported" : "Some profiles were not imported";
                await ShowMessageAsync(title, string.Join("\n\n", failures));
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Unable to open profiles", exception.Message);
        }
        finally
        {
            ImportProfileButton.IsEnabled = true;
        }
    }

    private async void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is ProfileNavigationItem item)
        {
            selectedProfile = item;
            await ShowProfileAsync(item);
        }
    }

    private async Task ShowProfileAsync(ProfileNavigationItem item)
    {
        if (item.Profile is not null)
        {
            ShowProfile(item, item.Profile);
            await UpdateActivitySummaryAsync(item);
            return;
        }

        if (item.ManagerName is not null && managerClient is not null)
        {
            try
            {
                var detail = await managerClient.RequestAsync<ManagerGetProfileRequest, ManagerProfileDetail>(
                    ManagerMethods.GetProfile,
                    new ManagerGetProfileRequest(item.ManagerName));
                ShowManagerProfile(item, detail);
                await UpdateActivitySummaryAsync(item);
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Profile details are unavailable", exception.Message);
            }
        }
    }

    private void ShowProfile(ProfileNavigationItem item, WireGuardProfile profile)
    {
        ProfileEmptyPanel.Visibility = Visibility.Collapsed;
        ProfileDetailPanel.Visibility = Visibility.Visible;
        RouterOSPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        SetSelectedState(RouterOSButton, RouterOSRail, false);
        SetSelectedState(SettingsButton, SettingsRail, false);

        ProfileNameText.Text = item.Name;
        SetProfileManagerControls(item);
        ProfileInterfaceNameText.Text = item.Name;
        ProfilePublicKeyText.Text = WireGuardKeyPair.FromPrivateKey(
            WireGuardConfigFormatter.PrivateKey(profile)).PublicKey;
        ProfilePeerPublicKeyText.Text = DisplayList(profile.Peers.Select(peer => peer.PublicKey));
        ProfileEndpointText.Text = DisplayList(
            profile.Peers
                .Select(peer => peer.Endpoint?.DisplayValue)
                .Where(value => value is not null)
                .Select(value => value!));
        ProfileAddressesText.Text = DisplayList(profile.Interface.Addresses.Select(address => address.Notation));
        ProfilePeersText.Text = profile.Peers.Count == 1 ? "1 peer" : $"{profile.Peers.Count} peers";
        ProfileRoutingModeText.Text = profile.DetectedRouteMode == TunnelRouteMode.Full
            ? "Full routing"
            : "Split routing";
        ProfileRoutesText.Text = DisplayList(profile.ImportedAllowedIps.Select(route => route.Notation));
        ProfileDnsServersText.Text = DisplayList(
            profile.DnsRouteSummary.Servers.Select(server => server.Address));
        ProfileDnsSearchText.Text = DisplayList(profile.Interface.DnsSearchDomains);
        ProfileKeepaliveText.Text = DisplayList(profile.Peers.Select(peer =>
            peer.PersistentKeepalive is null or 0
                ? "Off"
                : "every " + peer.PersistentKeepalive.Value + " seconds"));
        ProfileOnDemandText.Text = item.StoredProfile is null
            ? "Off"
            : item.StoredProfile.OnDemandEthernet || item.StoredProfile.OnDemandWiFi
                ? string.Join(", ", new[]
                {
                    item.StoredProfile.OnDemandEthernet ? "Ethernet" : null,
                    item.StoredProfile.OnDemandWiFi ? "Wi-Fi" : null,
                }.Where(value => value is not null))
                : "Off";
        UpdateProfilePolicyControls(item, profile);
        HooksWarningBorder.Visibility = profile.Interface.HasHooks ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateProfilePolicyControls(
        ProfileNavigationItem item,
        WireGuardProfile profile)
    {
        var isFull = item.StoredProfile?.RouteMode == StoredTunnelRouteMode.Full
            || item.StoredProfile is null
            && profile.DetectedRouteMode == TunnelRouteMode.Full;
        SetRouteSegmentState(isFull);
        ProfileRoutingHelpText.Text = isFull
            ? "Full tunnel sends all supported traffic through the VPN and blocks an unsupported address family."
            : "Split tunnel sends only the selected networks through the VPN.";
        var encryptedDns =
            item.StoredProfile?.DnsProtectionMode == StoredDnsProtectionMode.Encrypted;
        ProfileDnsModeText.Text = encryptedDns
            ? item.StoredProfile?.DnsProvider ?? "Encrypted DNS"
            : "Profile DNS";
        ProfileDnsHelpText.Text = encryptedDns
            ? "Use " + item.StoredProfile?.DnsResolverUrl + " while connected."
            : "Use the DNS servers saved in this WireGuard profile.";
        ProfileActivityText.Text = item.IsActive
            ? "Traffic metrics update while the tunnel is active."
            : "Connect this profile to begin recording traffic.";
    }

    private void SetRouteSegmentState(bool isFull)
    {
        ProfileFullButton.Background = isFull
            ? (Brush)Application.Current.Resources["NordicAccentBrush"]
            : (Brush)Application.Current.Resources["NordicRaisedBrush"];
        ProfileSplitButton.Background = isFull
            ? (Brush)Application.Current.Resources["NordicRaisedBrush"]
            : (Brush)Application.Current.Resources["NordicAccentBrush"];
    }

    private void ShowManagerProfile(ProfileNavigationItem item, ManagerProfileDetail profile)
    {
        ProfileEmptyPanel.Visibility = Visibility.Collapsed;
        ProfileDetailPanel.Visibility = Visibility.Visible;
        RouterOSPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        SetSelectedState(RouterOSButton, RouterOSRail, false);
        SetSelectedState(SettingsButton, SettingsRail, false);

        ProfileNameText.Text = item.Name;
        SetProfileManagerControls(item);
        ProfileInterfaceNameText.Text = item.Name;
        ProfilePublicKeyText.Text = "Managed by the installed tunnel service";
        ProfilePeerPublicKeyText.Text = DisplayList(profile.Peers.Select(peer => peer.PublicKey));
        ProfileEndpointText.Text = DisplayList(profile.Peers.Select(peer => peer.Endpoint).Where(value => value is not null).Select(value => value!));
        ProfileAddressesText.Text = DisplayList(profile.InterfaceAddresses);
        ProfilePeersText.Text = profile.Peers.Count == 1 ? "1 peer" : $"{profile.Peers.Count} peers";
        ProfileRoutingModeText.Text = profile.DetectedRouteMode == TunnelRouteMode.Full
            ? "Full routing"
            : "Split routing";
        ProfileRoutesText.Text = DisplayList(profile.Peers.SelectMany(peer => peer.AllowedIps));
        ProfileDnsServersText.Text = DisplayList(profile.DnsServers.Select(server => server.Address));
        ProfileDnsSearchText.Text = DisplayList(profile.DnsSearchDomains);
        ProfileKeepaliveText.Text = DisplayList(profile.Peers.Select(peer =>
            peer.PersistentKeepalive is null or 0
                ? "Off"
                : "every " + peer.PersistentKeepalive.Value + " seconds"));
        ProfileOnDemandText.Text = "Off";
        SetRouteSegmentState(profile.DetectedRouteMode == TunnelRouteMode.Full);
        ProfileRoutingHelpText.Text = profile.DetectedRouteMode == TunnelRouteMode.Full
            ? "Full tunnel sends all supported traffic through the VPN."
            : "Split tunnel sends only the configured networks through the VPN.";
        ProfileDnsModeText.Text = "Profile DNS";
        ProfileDnsHelpText.Text = "Use the DNS servers saved in this WireGuard profile.";
        ProfileActivityText.Text = item.IsActive
            ? "Traffic metrics update while the tunnel is active."
            : "Connect this profile to begin recording traffic.";
        HooksWarningBorder.Visibility = profile.HasHooks ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DisplayList(IEnumerable<string> values)
    {
        var displayedValues = values.Distinct(StringComparer.Ordinal).ToArray();
        return displayedValues.Length == 0 ? "Not specified" : string.Join("\n", displayedValues);
    }

    private void RouterOSButton_Click(object sender, RoutedEventArgs e) => ShowDestination(Destination.RouterOS);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowDestination(Destination.Settings);

    private void ShowDestination(Destination destination)
    {
        ProfilesList.SelectedItem = null;
        ProfileEmptyPanel.Visibility = destination == Destination.Profile ? Visibility.Visible : Visibility.Collapsed;
        ProfileDetailPanel.Visibility = Visibility.Collapsed;
        RouterOSPanel.Visibility = destination == Destination.RouterOS ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = destination == Destination.Settings ? Visibility.Visible : Visibility.Collapsed;

        SetSelectedState(RouterOSButton, RouterOSRail, destination == Destination.RouterOS);
        SetSelectedState(SettingsButton, SettingsRail, destination == Destination.Settings);
    }

    private static void SetSelectedState(Button button, Border rail, bool isSelected)
    {
        rail.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        button.Background = isSelected
            ? new SolidColorBrush(ColorHelper.FromArgb(41, 76, 131, 243))
            : new SolidColorBrush(Colors.Transparent);
        button.Foreground = isSelected
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 76, 131, 243))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 243, 247, 252));
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var content = new TextBlock
        {
            MaxWidth = 560,
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        };
        await ShowModalAsync(new ModalRequest
        {
            Title = title,
            Content = content,
            CancelText = "Done",
            MaxWidth = 640,
        });
    }

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowAboutAsync();
    }

    private async Task ShowAboutAsync()
    {
        var appVersion = FileVersionInfo.GetVersionInfo(
            typeof(App).Assembly.Location).ProductVersion ?? "Unknown";
        var backendPath = Path.Combine(AppContext.BaseDirectory, "wireguard.exe");
        var backendVersion = File.Exists(backendPath)
            ? FileVersionInfo.GetVersionInfo(backendPath).ProductVersion ?? "Unknown"
            : "Not installed";
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
        };
        content.Children.Add(new Image
        {
            Width = 112,
            Height = 112,
            Margin = new Thickness(0, 4, 0, 12),
            Source = new BitmapImage(new Uri(
                Path.Combine(AppContext.BaseDirectory, "Assets", "wireroute.png"))),
        });
        content.Children.Add(new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "WireRoute",
        });
        content.Children.Add(new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 16,
            Text = "App version: " + appVersion,
        });
        content.Children.Add(new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 14,
            Text = "Native backend: " + backendVersion
                + "  •  " + RuntimeInformation.ProcessArchitecture,
        });
        content.Children.Add(new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Text = "Copyright © 2026 WireRoute contributors."
                + Environment.NewLine
                + "Portions © 2018–2023 WireGuard LLC.",
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        await ShowModalAsync(new ModalRequest
        {
            Title = "About WireRoute",
            Content = content,
            CancelText = "Done",
            MaxWidth = 620,
        });
    }

    private TrayMenuSnapshot CreateTrayMenuSnapshot()
    {
        var availableProfiles = Profiles.Where(profile => profile.IsManaged || profile.IsStoredLocally).ToArray();
        foreach (var profile in availableProfiles.Where(profile => profile.IsStoredLocally && !profile.IsManaged))
        {
            profile.UpdateState(localTunnelController.GetState(profile.Name));
        }

        var status = availableProfiles.Any(profile =>
                profile.ManagerState == ManagerTunnelState.Starting
                || profile.LocalTunnelState == LocalTunnelState.Activating)
            ? "Status: Activating"
            : availableProfiles.Any(profile =>
                profile.ManagerState == ManagerTunnelState.Stopping
                || profile.LocalTunnelState == LocalTunnelState.Deactivating)
                ? "Status: Deactivating"
                : availableProfiles.Any(profile => profile.IsActive)
                    ? "Status: Active"
                    : "Status: Inactive";
        var tunnels = availableProfiles
            .Select(profile => new TrayTunnelMenuItem(
                profile.ManagerName ?? profile.StoredProfile!.Id.ToString("D"),
                profile.Name,
                profile.IsActive || profile.IsTransitioning,
                !profile.IsTransitioning
                    && (profile.IsManaged
                        ? profile.ManagerState switch
                        {
                            ManagerTunnelState.Started => managerCapabilities?.CanStopTunnels == true,
                            ManagerTunnelState.Stopped => managerCapabilities?.CanStartTunnels == true,
                            _ => false,
                        }
                        : localTunnelController.IsAvailable)))
            .ToArray();
        return new TrayMenuSnapshot(status, tunnels);
    }

    private async void ExecuteTrayMenuAction(TrayMenuAction action)
    {
        switch (action.Kind)
        {
            case TrayMenuActionKind.ToggleTunnel:
                var profile = Profiles.FirstOrDefault(item =>
                    item.ManagerName?.Equals(action.ManagerName, StringComparison.OrdinalIgnoreCase) == true
                    || item.StoredProfile?.Id.ToString("D").Equals(
                        action.ManagerName,
                        StringComparison.OrdinalIgnoreCase) == true);
                if (profile is not null)
                {
                    await ToggleProfileAsync(profile);
                }

                break;
            case TrayMenuActionKind.ManageTunnels:
                RestoreWindowFromTray();
                ShowManageTunnels();
                break;
            case TrayMenuActionKind.ImportTunnels:
                RestoreWindowFromTray();
                await ImportProfilesAsync();
                break;
            case TrayMenuActionKind.RouterOSPeerManager:
                RestoreWindowFromTray();
                ShowDestination(Destination.RouterOS);
                break;
            case TrayMenuActionKind.Settings:
                RestoreWindowFromTray();
                ShowDestination(Destination.Settings);
                break;
            case TrayMenuActionKind.About:
                RestoreWindowFromTray();
                await ShowAboutAsync();
                break;
            case TrayMenuActionKind.Quit:
                await QuitWireRouteAsync();
                break;
        }
    }

    private async Task QuitWireRouteAsync()
    {
        var encryptedDnsTunnel = Profiles.FirstOrDefault(profile =>
            profile.IsActive
            && profile.StoredProfile?.DnsProtectionMode == StoredDnsProtectionMode.Encrypted);
        if (encryptedDnsTunnel is not null)
        {
            RestoreWindowFromTray();
            await ShowMessageAsync(
                "Disconnect before quitting",
                "“" + encryptedDnsTunnel.Name + "” uses WireRoute's service-free encrypted DNS proxy. "
                + "Deactivate this tunnel before quitting so DNS remains available.");
            return;
        }

        if (Profiles.Any(profile => profile.IsActive))
        {
            RestoreWindowFromTray();
            var result = await ShowModalAsync(new ModalRequest
            {
                Title = "Quit WireRoute?",
                Content = new TextBlock
                {
                    MaxWidth = 560,
                    Text = "The active tunnel will remain connected after WireRoute quits.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryText = "Quit WireRoute",
                CancelText = "Cancel",
                MaxWidth = 620,
            });
            if (result != WireRouteModalResult.Primary)
            {
                return;
            }
        }

        if (managerClient is not null)
        {
            if (managerCapabilities?.CanQuitManager != true)
            {
                RestoreWindowFromTray();
                await ShowMessageAsync(
                    "WireRoute could not quit",
                    "The installed tunnel manager does not support intentional quit yet.");
                return;
            }

            try
            {
                _ = await managerClient.RequestAsync<ManagerQuitRequest, ManagerQuitResponse>(
                    ManagerMethods.QuitManager,
                    new ManagerQuitRequest(StopTunnels: false),
                    managerCancellation.Token);
            }
            catch (Exception exception)
            {
                RestoreWindowFromTray();
                await ShowMessageAsync("WireRoute could not quit", exception.Message);
                return;
            }
        }

        isExiting = true;
        Close();
    }

    private enum Destination
    {
        Profile,
        RouterOS,
        Settings,
    }
}
