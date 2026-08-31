using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WireRoute.App.Interop;
using WireRoute.App.Models;
using WireRoute.Core.Manager;
using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;

namespace WireRoute.App;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow appWindow;
    private readonly nint windowHandle;
    private readonly TrayIcon trayIcon;
    private readonly ManagerProtocolClient? managerClient;
    private readonly string? managerLaunchError;
    private ProfileNavigationItem? selectedProfile;
    private bool isExiting;

    public MainWindow(ManagerProtocolClient? managerClient = null, string? managerLaunchError = null)
    {
        this.managerClient = managerClient;
        this.managerLaunchError = managerLaunchError;
        InitializeComponent();
        Profiles.CollectionChanged += (_, _) => UpdateProfilesEmptyState();

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
        appWindow.Closing += AppWindow_Closing;
        UpdateProfilesEmptyState();
        _ = LoadRouterOSConnectionsAsync();
        _ = InitializeManagerAsync();
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

    private void UpdateProfilesEmptyState()
    {
        ProfilesEmptyState.Visibility = Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                    var item = new ProfileNavigationItem(profile);
                    Profiles.Add(item);
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
        ProfileDnsServersText.Text = DisplayList(profile.DnsRouteSummary.Servers.Select(server =>
            $"{server.Address} — {(server.Route == DnsServerRoute.ThroughTunnel ? "Through tunnel" : "Outside tunnel")}"));
        ProfileDnsSearchText.Text = DisplayList(profile.Interface.DnsSearchDomains);
        HooksWarningBorder.Visibility = profile.Interface.HasHooks ? Visibility.Visible : Visibility.Collapsed;
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
        ProfileEndpointText.Text = DisplayList(profile.Peers.Select(peer => peer.Endpoint).Where(value => value is not null).Select(value => value!));
        ProfileAddressesText.Text = DisplayList(profile.InterfaceAddresses);
        ProfilePeersText.Text = profile.Peers.Count == 1 ? "1 peer" : $"{profile.Peers.Count} peers";
        ProfileRoutingModeText.Text = profile.DetectedRouteMode == TunnelRouteMode.Full
            ? "Full routing"
            : "Split routing";
        ProfileRoutesText.Text = DisplayList(profile.Peers.SelectMany(peer => peer.AllowedIps));
        ProfileDnsServersText.Text = DisplayList(profile.DnsServers.Select(server =>
            $"{server.Address} — {(server.Route == DnsServerRoute.ThroughTunnel ? "Through tunnel" : "Outside tunnel")}"));
        ProfileDnsSearchText.Text = DisplayList(profile.DnsSearchDomains);
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
        await CreateDialog(title, content).ShowAsync();
    }

    private ContentDialog CreateDialog(string title, object content) => new()
    {
        XamlRoot = Root.XamlRoot,
        Title = title,
        Content = content,
        Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
        BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
        BorderThickness = new Thickness(1),
        CloseButtonText = "Done",
        CloseButtonStyle = (Style)Application.Current.Resources["NordicAccentButtonStyle"],
        DefaultButton = ContentDialogButton.None,
    };

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowAboutAsync();
    }

    private async Task ShowAboutAsync()
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "A native Windows client for clear, protected WireGuard routing.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Copyright © 2026 WireRoute contributors.\nPortions © 2018–2023 WireGuard LLC.",
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = CreateDialog("About WireRoute", content);
        await dialog.ShowAsync();
    }

    private TrayMenuSnapshot CreateTrayMenuSnapshot()
    {
        var managedProfiles = Profiles.Where(profile => profile.IsManaged).ToArray();
        var status = managedProfiles.Any(profile => profile.ManagerState == ManagerTunnelState.Starting)
            ? "Status: Activating"
            : managedProfiles.Any(profile => profile.ManagerState == ManagerTunnelState.Stopping)
                ? "Status: Deactivating"
                : managedProfiles.Any(profile => profile.ManagerState == ManagerTunnelState.Started)
                    ? "Status: Active"
                    : "Status: Inactive";
        var tunnels = managedProfiles
            .Select(profile => new TrayTunnelMenuItem(
                profile.ManagerName!,
                profile.Name,
                profile.ManagerState is ManagerTunnelState.Starting or ManagerTunnelState.Started,
                profile.ManagerState switch
                {
                    ManagerTunnelState.Started => managerCapabilities?.CanStopTunnels == true,
                    ManagerTunnelState.Stopped => managerCapabilities?.CanStartTunnels == true,
                    _ => false,
                }))
            .ToArray();
        return new TrayMenuSnapshot(status, tunnels);
    }

    private async void ExecuteTrayMenuAction(TrayMenuAction action)
    {
        switch (action.Kind)
        {
            case TrayMenuActionKind.ToggleTunnel:
                var profile = Profiles.FirstOrDefault(item =>
                    item.ManagerName?.Equals(action.ManagerName, StringComparison.OrdinalIgnoreCase) == true);
                if (profile is not null)
                {
                    await ToggleManagerProfileAsync(profile);
                }

                break;
            case TrayMenuActionKind.ManageTunnels:
                RestoreWindowFromTray();
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
        if (Profiles.Any(profile => profile.ManagerState == ManagerTunnelState.Started))
        {
            RestoreWindowFromTray();
            var confirmation = CreateDialog(
                "Quit WireRoute?",
                new TextBlock
                {
                    MaxWidth = 560,
                    Text = "The active tunnel will remain connected after WireRoute quits.",
                    TextWrapping = TextWrapping.Wrap,
                });
            confirmation.PrimaryButtonText = "Quit WireRoute";
            confirmation.PrimaryButtonStyle = (Style)Application.Current.Resources["NordicAccentButtonStyle"];
            confirmation.CloseButtonText = "Cancel";
            confirmation.CloseButtonStyle = null;
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
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
