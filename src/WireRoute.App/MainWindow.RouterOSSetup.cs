using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WireRoute.App.Models;
using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;
using WireRoute.RouterOS;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private readonly RouterOSProfileRecoveryStore routerOSProfileRecoveryStore = new();
    private IReadOnlyList<RouterOSProfileRecovery> routerOSProfileRecoveries = [];

    private async Task RefreshRouterOSProfileRecoveriesAsync()
    {
        try
        {
            routerOSProfileRecoveries = await routerOSProfileRecoveryStore.LoadAllAsync();
        }
        catch
        {
            routerOSProfileRecoveries = [];
        }

        UpdateRouterOSRecoveryAction();
    }

    private void UpdateRouterOSRecoveryAction()
    {
        if (RouterOSRecoverProfileButton is null)
        {
            return;
        }

        var peer = (RouterOSDiscoveryList.SelectedItem as Models.RouterOSDiscoveryRow)?.Peer;
        var context = routerOSConnectedContext;
        var pending = peer is null || context is null
            ? null
            : PendingRouterOSProfileRecovery(context.Connection.Id, peer.Id);
        var matchingProfile = peer is null ? null : ProfileMatchingRouterOSPeer(peer);
        var profileKeyUnavailable = Profiles.Any(value =>
            string.IsNullOrWhiteSpace(value.InterfacePublicKey));
        RouterOSRecoverProfileButton.Content = pending is null
            ? "Recover Profile…"
            : "Resume Recovery…";
        RouterOSRecoverProfileButton.IsEnabled = context is not null
            && peer is not null
            && !isRouterOSBusy
            && matchingProfile is null
            && !profileKeyUnavailable
            && RouterOSPeerCreation.IsWireGuardKey(peer.PublicKey);
        ToolTipService.SetToolTip(
            RouterOSRecoverProfileButton,
            context is null
                ? "Connect to a RouterOS device first."
                : peer is null
                    ? "Select the RouterOS peer whose local private profile was lost."
                    : matchingProfile is not null
                        ? $"WireRoute already has the matching profile ‘{matchingProfile.Name}’."
                        : profileKeyUnavailable
                            ? "At least one loaded profile does not expose a verifiable public key. Repair it, or update and restart an older tunnel manager, before recovering a peer."
                            : pending is not null
                                ? "Continue the protected recovery already prepared for this router and peer."
                                : !RouterOSPeerCreation.IsWireGuardKey(peer.PublicKey)
                                    ? "RouterOS did not return a valid WireGuard public key for this peer."
                                    : "Generate a replacement key locally, update only this peer’s public key, and restore its WireRoute profile.");
    }

    private RouterOSProfileRecovery? PendingRouterOSProfileRecovery(Guid connectionId, string peerId) =>
        routerOSProfileRecoveries
            .Where(value => value.IsPeerKeyReplacement
                && value.RouterConnectionId == connectionId
                && value.RouterPeerId?.Equals(peerId, StringComparison.Ordinal) == true)
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefault();

    private ProfileNavigationItem? ProfileMatchingRouterOSPeer(RouterOSWireGuardPeer peer) =>
        Profiles.FirstOrDefault(value =>
            value.InterfacePublicKey?.Equals(peer.PublicKey, StringComparison.Ordinal) == true);

    private RouterOSWireGuardPeer? SelectedRouterOSPeer() =>
        (RouterOSDiscoveryList.SelectedItem as RouterOSDiscoveryRow)?.Peer;

    private void RouterOSPeerContextFlyout_Opening(object sender, object e)
    {
        UpdateRouterOSRecoveryAction();
        var peer = SelectedRouterOSPeer();
        var profile = peer is null ? null : ProfileMatchingRouterOSPeer(peer);
        var hasLocalConfiguration = profile?.StoredProfile is not null && profile.Profile is not null;

        RouterOSPeerOpenMenuItem.IsEnabled = profile is not null;
        RouterOSPeerQrMenuItem.IsEnabled = hasLocalConfiguration;
        RouterOSPeerExportMenuItem.IsEnabled = hasLocalConfiguration;
        RouterOSPeerCopyPublicKeyMenuItem.IsEnabled = peer is not null;
        RouterOSPeerCopyPrivateKeyMenuItem.IsEnabled = hasLocalConfiguration;
        RouterOSPeerRecoveryMenuItem.Text = RouterOSRecoverProfileButton.Content?.ToString()
            ?? "Recover Missing Profile…";
        RouterOSPeerRecoveryMenuItem.IsEnabled = RouterOSRecoverProfileButton.IsEnabled;
    }

    private async void RouterOSPeerContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRouterOSPeer() is not { } peer
            || ProfileMatchingRouterOSPeer(peer) is not { } profile)
        {
            return;
        }

        if (ReferenceEquals(ProfilesList.SelectedItem, profile))
        {
            selectedProfile = profile;
            await ShowProfileAsync(profile);
        }
        else
        {
            ProfilesList.SelectedItem = profile;
        }
    }

    private void RouterOSPeerContextQr_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRouterOSPeer() is { } peer
            && ProfileMatchingRouterOSPeer(peer) is { } profile)
        {
            selectedProfile = profile;
            ProfileContextQr_Click(sender, e);
        }
    }

    private void RouterOSPeerContextExport_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRouterOSPeer() is { } peer
            && ProfileMatchingRouterOSPeer(peer) is { } profile)
        {
            selectedProfile = profile;
            ProfileContextExportSelected_Click(sender, e);
        }
    }

    private async void RouterOSPeerContextCopyPublicKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRouterOSPeer() is { } peer)
        {
            await CopySensitiveTextAsync(
                peer.PublicKey,
                "Peer public key copied",
                "The RouterOS peer public key is on the clipboard.");
        }
    }

    private void RouterOSPeerContextCopyPrivateKey_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRouterOSPeer() is { } peer
            && ProfileMatchingRouterOSPeer(peer) is { } profile)
        {
            selectedProfile = profile;
            ProfileContextCopyPrivateKey_Click(sender, e);
        }
    }

    private void RouterOSPeerContextRecovery_Click(object sender, RoutedEventArgs e) =>
        RouterOSRecoverProfileButton_Click(sender, e);

    private async void RouterOSRecoverProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (routerOSConnectedContext is not { } context
            || (RouterOSDiscoveryList.SelectedItem as Models.RouterOSDiscoveryRow)?.Peer is not { } peer)
        {
            return;
        }

        var pending = PendingRouterOSProfileRecovery(context.Connection.Id, peer.Id);
        if (pending is not null)
        {
            await ResumeRouterOSProfileRecoveryAsync(peer, pending);
            return;
        }

        if (ProfileMatchingRouterOSPeer(peer) is { } existing)
        {
            await ShowMessageAsync(
                "Profile already available",
                $"‘{existing.Name}’ already contains the private key matching this RouterOS peer.");
            UpdateRouterOSRecoveryAction();
            return;
        }

        var routerInterface = routerOSInterfaces.FirstOrDefault(value =>
            value.Name.Equals(peer.InterfaceName, StringComparison.Ordinal));
        if (routerInterface is null)
        {
            await ShowMessageAsync(
                "Peer interface is unavailable",
                $"RouterOS interface {peer.InterfaceName} is not present in the current discovery results. Refresh the connection and try again.");
            return;
        }

        WireGuardKeyPair keyPair;
        try
        {
            keyPair = WireGuardKeyPair.Generate();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Replacement key could not be generated", exception.Message);
            return;
        }

        var state = CreateInitialRecoveryState(context.Connection, peer, routerInterface);
        while (true)
        {
            var proposal = await ShowRouterOSPeerSetupAsync(state, keyPair, peer);
            if (proposal is null)
            {
                return;
            }

            var review = await ShowRouterOSPeerReviewAsync(proposal);
            if (review == WireRouteModalResult.Secondary)
            {
                continue;
            }

            if (review != WireRouteModalResult.Primary)
            {
                return;
            }

            await BeginRouterOSProfileRecoveryAsync(proposal);
            return;
        }
    }

    private async void RouterOSSetUpPeerButton_Click(object sender, RoutedEventArgs e)
    {
        if (routerOSConnectedContext is null || routerOSInterfaces.Count == 0)
        {
            return;
        }

        WireGuardKeyPair keyPair;
        try
        {
            keyPair = WireGuardKeyPair.Generate();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Client key could not be generated", exception.Message);
            return;
        }

        var state = CreateInitialSetupState(routerOSConnectedContext.Connection);
        while (true)
        {
            var proposal = await ShowRouterOSPeerSetupAsync(state, keyPair);
            if (proposal is null)
            {
                return;
            }

            var review = await ShowRouterOSPeerReviewAsync(proposal);
            if (review == WireRouteModalResult.Secondary)
            {
                continue;
            }

            if (review != WireRouteModalResult.Primary)
            {
                return;
            }

            await CreateRouterOSPeerAndImportAsync(proposal);
            return;
        }
    }

    private RouterOSPeerSetupState CreateInitialSetupState(RouterOSStoredConnection connection)
    {
        var selectedInterface = routerOSInterfaces.FirstOrDefault(value =>
                value.Name.Equals(connection.DefaultInterface, StringComparison.Ordinal))
            ?? routerOSInterfaces.FirstOrDefault(value => value.IsRunning && !value.IsDisabled)
            ?? routerOSInterfaces.First();
        var addressSuggestion = RouterOSClientAddressSuggestion.Discover(selectedInterface.Name, routerOSPeers);
        return new RouterOSPeerSetupState
        {
            InterfaceName = selectedInterface.Name,
            ClientAddress = addressSuggestion?.Address.Notation ?? string.Empty,
            LastSuggestedClientAddress = addressSuggestion?.Address.Notation,
            EndpointAddress = string.IsNullOrWhiteSpace(appSettings.PreferredEndpoint)
                ? routerOSPublicEndpointSuggestion?.Address ?? string.Empty
                : appSettings.PreferredEndpoint,
            EndpointPort = selectedInterface.ListenPort?.ToString() ?? string.Empty,
            LastSuggestedEndpointPort = selectedInterface.ListenPort?.ToString(),
            DnsServers = appSettings.DnsServers,
            Routes = appSettings.SplitTunnelRoutes,
            PersistentKeepalive = appSettings.PersistentKeepalive.ToString(),
        };
    }

    private RouterOSPeerSetupState CreateInitialRecoveryState(
        RouterOSStoredConnection connection,
        RouterOSWireGuardPeer peer,
        RouterOSWireGuardInterface routerInterface)
    {
        var address = RouterOSMissingProfileRecoveryValidator.SuggestedClientAddress(peer)?.Notation;
        var peerName = peer.Name?.Trim();
        if (string.IsNullOrWhiteSpace(peerName)
            && !RouterOSPeerCreation.IsWireRouteManagedComment(peer.Comment))
        {
            peerName = peer.Comment?.Trim();
        }

        return new RouterOSPeerSetupState
        {
            InterfaceName = routerInterface.Name,
            Name = string.IsNullOrWhiteSpace(peerName) ? "Recovered Profile" : peerName,
            ClientAddress = address ?? string.Empty,
            LastSuggestedClientAddress = address,
            EndpointAddress = string.IsNullOrWhiteSpace(appSettings.PreferredEndpoint)
                ? routerOSPublicEndpointSuggestion?.Address ?? string.Empty
                : appSettings.PreferredEndpoint,
            EndpointPort = routerInterface.ListenPort?.ToString() ?? string.Empty,
            LastSuggestedEndpointPort = routerInterface.ListenPort?.ToString(),
            DnsServers = appSettings.DnsServers,
            RouteModeIndex = string.IsNullOrWhiteSpace(appSettings.SplitTunnelRoutes) ? 1 : 0,
            Routes = appSettings.SplitTunnelRoutes,
            PersistentKeepalive = appSettings.PersistentKeepalive.ToString(),
        };
    }

    private async Task<RouterOSPeerSetupProposal?> ShowRouterOSPeerSetupAsync(
        RouterOSPeerSetupState state,
        WireGuardKeyPair keyPair,
        RouterOSWireGuardPeer? recoveryPeer = null)
    {
        var isRecovery = recoveryPeer is not null;
        var interfacePicker = new ComboBox
        {
            DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = routerOSInterfaces,
        };
        interfacePicker.SelectedItem = routerOSInterfaces.FirstOrDefault(value =>
            value.Name.Equals(state.InterfaceName, StringComparison.Ordinal));
        interfacePicker.IsEnabled = !isRecovery;
        var nameField = new TextBox
        {
            PlaceholderText = "Laptop",
            Text = state.Name,
        };
        var addressField = new TextBox
        {
            PlaceholderText = "10.0.0.2/32",
            Text = state.ClientAddress,
        };
        var addressHelp = SetupHelpText(string.Empty);
        var endpointField = new TextBox
        {
            PlaceholderText = "vpn.example.com",
            Text = state.EndpointAddress,
        };
        var endpointPortField = new TextBox
        {
            PlaceholderText = "51820",
            Text = state.EndpointPort,
        };
        var dnsField = new TextBox
        {
            PlaceholderText = "1.1.1.1, 9.9.9.9",
            Text = state.DnsServers,
        };
        var splitRouteButton = new Button
        {
            Content = "Split",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var fullRouteButton = new Button
        {
            Content = "Full",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var routeModeGrid = new Grid
        {
            Height = 40,
            Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
            CornerRadius = new CornerRadius(6),
        };
        routeModeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        routeModeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(fullRouteButton, 1);
        routeModeGrid.Children.Add(splitRouteButton);
        routeModeGrid.Children.Add(fullRouteButton);
        var routeModeIndex = state.RouteModeIndex;
        var routesField = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 110,
            PlaceholderText = "10.0.0.0/8, 192.168.0.0/16",
            Text = state.Routes,
            TextWrapping = TextWrapping.Wrap,
        };
        var routesHelp = new StackPanel { Spacing = 4 };
        routesHelp.Children.Add(new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Not sure what to enter?",
        });
        routesHelp.Children.Add(SetupHelpText(
            "Add networks that exist behind this VPN, such as your home, office, or VPN address range.\n"
            + "Examples only — replace them with your networks:\n192.168.50.0/24\n10.20.0.0/16"));
        var keepaliveField = new TextBox
        {
            Width = 120,
            Text = state.PersistentKeepalive,
        };
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        void UpdateInterfaceSuggestions()
        {
            if (interfacePicker.SelectedItem is not RouterOSWireGuardInterface selected)
            {
                addressHelp.Text = string.Empty;
                return;
            }

            var currentAddress = addressField.Text.Trim();
            if (currentAddress.Length == 0 || currentAddress == state.LastSuggestedClientAddress)
            {
                if (recoveryPeer is null)
                {
                    var suggestion = RouterOSClientAddressSuggestion.Discover(selected.Name, routerOSPeers);
                    state.LastSuggestedClientAddress = suggestion?.Address.Notation;
                    addressField.Text = suggestion?.Address.Notation ?? string.Empty;
                    addressHelp.Text = suggestion is null
                        ? $"No unambiguous /32 could be suggested for {selected.Name}. Enter the client address manually."
                        : $"Suggested from existing /32 peers on {selected.Name}. Verify it before continuing.";
                }
                else
                {
                    var suggestion = RouterOSMissingProfileRecoveryValidator.SuggestedClientAddress(recoveryPeer);
                    state.LastSuggestedClientAddress = suggestion?.Notation;
                    addressField.Text = suggestion?.Notation ?? string.Empty;
                    addressHelp.Text = suggestion is null
                        ? "This peer has zero or multiple exact host addresses. Enter the correct /32 or /128 from its RouterOS allowed-address entries."
                        : "Restored from this peer’s exact RouterOS /32 or /128 host address.";
                }
            }
            else
            {
                addressHelp.Text = "Using the manually entered client address.";
            }

            var currentPort = endpointPortField.Text.Trim();
            if (currentPort.Length == 0 || currentPort == state.LastSuggestedEndpointPort)
            {
                state.LastSuggestedEndpointPort = selected.ListenPort?.ToString();
                endpointPortField.Text = state.LastSuggestedEndpointPort ?? string.Empty;
            }
        }

        void UpdateRouteMode()
        {
            routesField.IsEnabled = routeModeIndex == 0;
            routesField.Opacity = routesField.IsEnabled ? 1 : 0.6;
            splitRouteButton.Background = routeModeIndex == 0
                ? (Brush)Application.Current.Resources["NordicAccentBrush"]
                : (Brush)Application.Current.Resources["NordicRaisedBrush"];
            fullRouteButton.Background = routeModeIndex == 1
                ? (Brush)Application.Current.Resources["NordicAccentBrush"]
                : (Brush)Application.Current.Resources["NordicRaisedBrush"];
        }

        interfacePicker.SelectionChanged += (_, _) => UpdateInterfaceSuggestions();
        splitRouteButton.Click += (_, _) => { routeModeIndex = 0; UpdateRouteMode(); };
        fullRouteButton.Click += (_, _) => { routeModeIndex = 1; UpdateRouteMode(); };
        UpdateInterfaceSuggestions();
        UpdateRouteMode();

        var endpointGrid = new Grid { ColumnSpacing = 10 };
        endpointGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        endpointGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        Grid.SetColumn(endpointPortField, 1);
        endpointGrid.Children.Add(endpointField);
        endpointGrid.Children.Add(endpointPortField);
        var addressStack = new StackPanel { Spacing = 4 };
        addressStack.Children.Add(addressField);
        addressStack.Children.Add(addressHelp);
        var routeStack = new StackPanel { Spacing = 10 };
        routeStack.Children.Add(routeModeGrid);
        routeStack.Children.Add(routesField);
        routeStack.Children.Add(ModalCard(routesHelp, raised: true));
        var keepaliveStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keepaliveStack.Children.Add(keepaliveField);
        keepaliveStack.Children.Add(new TextBlock
        {
            Text = "seconds",
            VerticalAlignment = VerticalAlignment.Center,
        });

        var formGrid = new Grid { ColumnSpacing = 20, RowSpacing = 12 };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < 7; row++)
        {
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        void AddSetupRow(int row, string label, FrameworkElement field)
        {
            var labelText = new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = label,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 9, 0, 0),
            };
            Grid.SetRow(labelText, row);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            formGrid.Children.Add(labelText);
            formGrid.Children.Add(field);
        }

        AddSetupRow(0, "Interface", interfacePicker);
        AddSetupRow(1, "Device name", nameField);
        AddSetupRow(2, "Client address", addressStack);
        AddSetupRow(3, "Public endpoint / port", endpointGrid);
        AddSetupRow(4, "DNS servers", dnsField);
        AddSetupRow(5, "Client routes", routeStack);
        AddSetupRow(6, "Keepalive", keepaliveStack);

        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            Text = isRecovery
                ? "The original private key cannot be recovered. WireRoute generated a replacement locally; only its public key can be sent to RouterOS."
                : "WireRoute generated this device’s private key locally. It will never be sent to RouterOS.",
            TextWrapping = TextWrapping.Wrap,
        });
        form.Children.Add(formGrid);
        form.Children.Add(errorText);
        RouterOSPeerSetupProposal? proposal = null;
        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = isRecovery ? "Recover Missing Profile" : "Set Up a Device",
            Subtitle = isRecovery
                ? "Rebuild the local profile from RouterOS, review it, then replace only this peer’s public key."
                : "WireRoute generates a fresh key pair locally, suggests safe values from RouterOS, prefills your defaults, and lets you review the exact change.",
            Content = ModalCard(form),
            PrimaryText = isRecovery ? "Review Recovery" : "Review Peer",
            CancelText = "Cancel",
            MaxWidth = 960,
            OnPrimary = () =>
            {
                try
                {
                    proposal = recoveryPeer is null
                        ? MakeRouterOSPeerProposal(
                            state,
                            keyPair,
                            interfacePicker.SelectedItem as RouterOSWireGuardInterface,
                            nameField.Text,
                            addressField.Text,
                            endpointField.Text,
                            endpointPortField.Text,
                            dnsField.Text,
                            routeModeIndex,
                            routesField.Text,
                            keepaliveField.Text)
                        : MakeRouterOSRecoveryProposal(
                            state,
                            keyPair,
                            recoveryPeer,
                            interfacePicker.SelectedItem as RouterOSWireGuardInterface,
                            nameField.Text,
                            addressField.Text,
                            endpointField.Text,
                            endpointPortField.Text,
                            dnsField.Text,
                            routeModeIndex,
                            routesField.Text,
                            keepaliveField.Text);
                    return Task.FromResult(true);
                }
                catch (Exception exception)
                {
                    return Task.FromResult(
                        KeepModalOpen(request!, errorText, exception.Message));
                }
            },
        };
        var result = await ShowModalAsync(request);
        return result == WireRouteModalResult.Primary ? proposal : null;
    }

    private RouterOSPeerSetupProposal MakeRouterOSPeerProposal(
        RouterOSPeerSetupState state,
        WireGuardKeyPair keyPair,
        RouterOSWireGuardInterface? selectedInterface,
        string name,
        string clientAddress,
        string endpointAddress,
        string endpointPortText,
        string dnsServers,
        int routeModeIndex,
        string routes,
        string persistentKeepaliveText)
    {
        if (selectedInterface is null)
        {
            throw new ArgumentException("Select a WireGuard interface.");
        }

        if (!int.TryParse(endpointPortText.Trim(), out var endpointPort))
        {
            throw new RouterOSProvisioningException(
                RouterOSProvisioningError.InvalidEndpointPort,
                "The endpoint port must be between 1 and 65535.");
        }

        if (!int.TryParse(persistentKeepaliveText.Trim(), out var persistentKeepalive))
        {
            throw new RouterOSProvisioningException(
                RouterOSProvisioningError.InvalidPersistentKeepalive,
                "Persistent keepalive must be between 0 and 65535 seconds.");
        }

        var peerCreation = new RouterOSPeerCreation(
            selectedInterface.Name,
            name,
            RouterOSPeerCreation.WireRouteManagedComment,
            keyPair.PublicKey,
            clientAddress,
            persistentKeepalive,
            existingPeers: routerOSPeers);
        if (Profiles.Any(item => WireRouteStoredProfile.DisplayNamesEqual(item.Name, peerCreation.Name)))
        {
            throw new ArgumentException($"A WireRoute profile named ‘{peerCreation.Name}’ already exists.");
        }

        var allowedIps = routeModeIndex == 1
            ? new[] { peerCreation.ClientAddress.Family == IpFamily.Ipv4 ? "0.0.0.0/0" : "::/0" }
            : SplitSetupValues(routes);
        var clientConfiguration = new WireGuardClientConfiguration(
            peerCreation.Name,
            keyPair.PrivateKey,
            peerCreation.ClientAddress.Notation,
            SplitSetupValues(dnsServers),
            selectedInterface.PublicKey,
            endpointAddress,
            endpointPort,
            allowedIps,
            persistentKeepalive);
        var profileId = Guid.NewGuid();
        var tunnelName = WireRouteStoredProfile.CreateTunnelName(peerCreation.Name, profileId);
        _ = WireGuardConfigParser.Parse(clientConfiguration.WgQuickConfiguration, tunnelName);

        state.InterfaceName = selectedInterface.Name;
        state.Name = name;
        state.ClientAddress = clientAddress;
        state.EndpointAddress = endpointAddress;
        state.EndpointPort = endpointPortText;
        state.DnsServers = dnsServers;
        state.RouteModeIndex = routeModeIndex;
        state.Routes = routes;
        state.PersistentKeepalive = persistentKeepaliveText;
        return new RouterOSPeerSetupProposal(
            peerCreation,
            null,
            keyPair.PublicKey,
            clientConfiguration,
            profileId,
            tunnelName);
    }

    private RouterOSPeerSetupProposal MakeRouterOSRecoveryProposal(
        RouterOSPeerSetupState state,
        WireGuardKeyPair keyPair,
        RouterOSWireGuardPeer recoveryPeer,
        RouterOSWireGuardInterface? selectedInterface,
        string name,
        string clientAddress,
        string endpointAddress,
        string endpointPortText,
        string dnsServers,
        int routeModeIndex,
        string routes,
        string persistentKeepaliveText)
    {
        if (selectedInterface is null)
        {
            throw new ArgumentException("The peer’s WireGuard interface is unavailable.");
        }

        var recoveredAddress = RouterOSMissingProfileRecoveryValidator.Validate(
            recoveryPeer,
            selectedInterface,
            clientAddress);
        if (!int.TryParse(endpointPortText.Trim(), out var endpointPort))
        {
            throw new RouterOSProvisioningException(
                RouterOSProvisioningError.InvalidEndpointPort,
                "The endpoint port must be between 1 and 65535.");
        }

        if (!int.TryParse(persistentKeepaliveText.Trim(), out var persistentKeepalive))
        {
            throw new RouterOSProvisioningException(
                RouterOSProvisioningError.InvalidPersistentKeepalive,
                "Persistent keepalive must be between 0 and 65535 seconds.");
        }

        if (keyPair.PublicKey.Equals(recoveryPeer.PublicKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Generate a different replacement key before continuing.");
        }

        var displayName = name.Trim();
        if (Profiles.Any(item => WireRouteStoredProfile.DisplayNamesEqual(item.Name, displayName)))
        {
            throw new ArgumentException($"A WireRoute profile named ‘{displayName}’ already exists.");
        }

        var allowedIps = routeModeIndex == 1
            ? new[] { recoveredAddress.Family == IpFamily.Ipv4 ? "0.0.0.0/0" : "::/0" }
            : SplitSetupValues(routes);
        var clientConfiguration = new WireGuardClientConfiguration(
            displayName,
            keyPair.PrivateKey,
            recoveredAddress.Notation,
            SplitSetupValues(dnsServers),
            selectedInterface.PublicKey,
            endpointAddress,
            endpointPort,
            allowedIps,
            persistentKeepalive);
        var profileId = Guid.NewGuid();
        var tunnelName = WireRouteStoredProfile.CreateTunnelName(clientConfiguration.Name, profileId);
        _ = WireGuardConfigParser.Parse(clientConfiguration.WgQuickConfiguration, tunnelName);

        state.InterfaceName = selectedInterface.Name;
        state.Name = name;
        state.ClientAddress = clientAddress;
        state.EndpointAddress = endpointAddress;
        state.EndpointPort = endpointPortText;
        state.DnsServers = dnsServers;
        state.RouteModeIndex = routeModeIndex;
        state.Routes = routes;
        state.PersistentKeepalive = persistentKeepaliveText;
        return new RouterOSPeerSetupProposal(
            null,
            recoveryPeer,
            keyPair.PublicKey,
            clientConfiguration,
            profileId,
            tunnelName);
    }

    private Task<WireRouteModalResult> ShowRouterOSPeerReviewAsync(RouterOSPeerSetupProposal proposal)
    {
        var recoveryPeer = proposal.RecoveryPeer;
        var peerCreation = proposal.PeerCreation;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = recoveryPeer is null
                ? "WireRoute will add exactly one WireGuard peer. It will not change RouterOS addresses, routes, firewall rules, or NAT."
                : "WireRoute will replace only this peer’s public key. It will not change RouterOS addresses, routes, firewall rules, NAT, or any other peer.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(ReviewDetail("Device", proposal.ClientConfiguration.Name));
        content.Children.Add(ReviewDetail(
            "Interface",
            recoveryPeer?.InterfaceName ?? peerCreation?.InterfaceName ?? "—"));
        content.Children.Add(ReviewDetail(
            "Client address",
            proposal.ClientConfiguration.ClientAddress.Notation));
        content.Children.Add(ReviewDetail(
            "Endpoint",
            $"{proposal.ClientConfiguration.EndpointAddress}:{proposal.ClientConfiguration.EndpointPort}"));
        content.Children.Add(ReviewDetail(
            "Client routes",
            string.Join(", ", proposal.ClientConfiguration.AllowedIps.Select(value => value.Notation))));
        if (recoveryPeer is not null)
        {
            content.Children.Add(ReviewDetail("Current peer key", recoveryPeer.PublicKey));
            content.Children.Add(ReviewDetail("Replacement key", proposal.ClientPublicKey));
        }
        return ShowModalAsync(new ModalRequest
        {
            Title = recoveryPeer is null ? "Review RouterOS Peer" : "Review Profile Recovery",
            Subtitle = recoveryPeer is null
                ? "Confirm the exact RouterOS change and matching local WireGuard profile."
                : "Confirm the replacement profile and the only field WireRoute will change on RouterOS.",
            Content = ModalCard(content),
            PrimaryText = recoveryPeer is null ? "Add Peer" : "Recover Profile",
            SecondaryText = "Back",
            CancelText = "Cancel",
            MaxWidth = 760,
        });
    }

    private async Task BeginRouterOSProfileRecoveryAsync(RouterOSPeerSetupProposal proposal)
    {
        var context = routerOSConnectedContext;
        var peer = proposal.RecoveryPeer;
        if (context is null || peer is null)
        {
            return;
        }

        var recovery = new RouterOSProfileRecovery(
            Guid.NewGuid(),
            proposal.ClientConfiguration.Name,
            proposal.ClientConfiguration.WgQuickConfiguration,
            DateTimeOffset.UtcNow,
            RouterOSProfileRecoveryReason.PendingRouterKeyReplacement)
        {
            RouterConnectionId = context.Connection.Id,
            RouterPeerId = peer.Id,
            OriginalPeerPublicKey = peer.PublicKey,
            ReplacementPublicKey = proposal.ClientPublicKey,
            ProfileId = proposal.ProfileId,
            TunnelName = proposal.TunnelName,
        };
        try
        {
            await routerOSProfileRecoveryStore.SaveAsync(recovery);
            await RefreshRouterOSProfileRecoveriesAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Profile recovery was stopped safely",
                $"The replacement configuration could not be protected before changing RouterOS. No router changes were made.\n\n{exception.Message}");
            return;
        }

        await ContinueRouterOSProfileRecoveryAsync(peer, recovery);
    }

    private async Task ResumeRouterOSProfileRecoveryAsync(
        RouterOSWireGuardPeer selectedPeer,
        RouterOSProfileRecovery recovery)
    {
        if (routerOSConnectedContext is not { } context
            || recovery.RouterConnectionId != context.Connection.Id
            || recovery.RouterPeerId?.Equals(selectedPeer.Id, StringComparison.Ordinal) != true
            || recovery.ProfileId is null
            || string.IsNullOrWhiteSpace(recovery.TunnelName)
            || !RouterOSPeerCreation.IsWireGuardKey(recovery.OriginalPeerPublicKey ?? string.Empty)
            || !RouterOSPeerCreation.IsWireGuardKey(recovery.ReplacementPublicKey ?? string.Empty))
        {
            await ShowConfigurationRecoveryAsync(
                recovery,
                "Recovery metadata needs attention",
                "This protected recovery does not contain complete router and peer identity metadata, so WireRoute will not change RouterOS automatically.");
            return;
        }

        var originalPeerPublicKey = recovery.OriginalPeerPublicKey!;
        var replacementPublicKey = recovery.ReplacementPublicKey!;

        try
        {
            var parsed = WireGuardConfigParser.Parse(
                recovery.WgQuickConfiguration,
                recovery.TunnelName);
            var derivedKey = WireGuardKeyPair.FromPrivateKey(
                WireGuardConfigFormatter.PrivateKey(parsed)).PublicKey;
            if (!derivedKey.Equals(replacementPublicKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The protected profile does not match its recorded replacement public key.");
            }
        }
        catch (Exception exception)
        {
            await ShowConfigurationRecoveryAsync(
                recovery,
                "Protected recovery could not be verified",
                $"WireRoute will not change RouterOS because the retained profile failed integrity validation. {exception.Message}");
            return;
        }

        if (Profiles.FirstOrDefault(value =>
                value.InterfacePublicKey?.Equals(
                    recovery.ReplacementPublicKey,
                    StringComparison.Ordinal) == true) is { } matchingProfile)
        {
            var cleanupMessage = await TryDeleteProfileRecoveryAsync(recovery.Id);
            SetRouterOSStatus(
                $"Recovery is already complete in profile ‘{matchingProfile.Name}’.{cleanupMessage}",
                isSuccess: cleanupMessage.Length == 0);
            return;
        }

        var currentPeer = routerOSPeers.FirstOrDefault(value =>
            value.Id.Equals(selectedPeer.Id, StringComparison.Ordinal)) ?? selectedPeer;
        var keyState = RouterOSPeerKeyRecoveryReconciler.Reconcile(
            currentPeer.PublicKey,
            originalPeerPublicKey,
            replacementPublicKey);
        if (keyState == RouterOSPeerKeyRecoveryState.Conflict)
        {
            var updated = recovery with
            {
                Reason = RouterOSProfileRecoveryReason.RouterKeyReplacementUncertain,
            };
            var storageMessage = await TrySaveProfileRecoveryAsync(updated);
            await ShowConfigurationRecoveryAsync(
                updated,
                "RouterOS peer key changed",
                $"This peer now has a third public key that matches neither the original nor the protected replacement. WireRoute made no changes. Reconcile the peer manually before resuming.{storageMessage}");
            return;
        }

        var routerHasReplacementKey = keyState == RouterOSPeerKeyRecoveryState.ReplacementConfirmed;

        if (Profiles.Any(value =>
                WireRouteStoredProfile.DisplayNamesEqual(value.Name, recovery.DisplayName)
                && value.InterfacePublicKey?.Equals(
                    recovery.ReplacementPublicKey,
                    StringComparison.Ordinal) != true))
        {
            await ShowConfigurationRecoveryAsync(
                recovery,
                "Profile name is now in use",
                $"Another profile named ‘{recovery.DisplayName}’ exists. WireRoute made no RouterOS changes; save the protected configuration and resolve the name conflict before retrying.");
            return;
        }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = routerHasReplacementKey
                ? "RouterOS already has the protected replacement public key. WireRoute will leave the router unchanged and finish importing the matching private profile."
                : "RouterOS still has the original public key. WireRoute will replace only that key, verify the response, and import the matching private profile.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(ReviewDetail("Peer", FirstNonempty(currentPeer.Name, currentPeer.Comment) ?? currentPeer.Id));
        content.Children.Add(ReviewDetail("Profile", recovery.DisplayName));
        content.Children.Add(ReviewDetail("Current peer key", currentPeer.PublicKey));
        content.Children.Add(ReviewDetail("Protected key", replacementPublicKey));
        var result = await ShowModalAsync(new ModalRequest
        {
            Title = "Resume Profile Recovery",
            Subtitle = "WireRoute reconciled the protected recovery with the current RouterOS peer before offering this action.",
            Content = ModalCard(content),
            PrimaryText = routerHasReplacementKey ? "Finish Recovery" : "Resume Recovery",
            CancelText = "Cancel",
            MaxWidth = 780,
        });
        if (result == WireRouteModalResult.Primary)
        {
            await ContinueRouterOSProfileRecoveryAsync(currentPeer, recovery);
        }
    }

    private async Task ContinueRouterOSProfileRecoveryAsync(
        RouterOSWireGuardPeer selectedPeer,
        RouterOSProfileRecovery recovery)
    {
        var context = routerOSConnectedContext;
        if (context is null
            || !Uri.TryCreate(context.Connection.Url, UriKind.Absolute, out var url)
            || recovery.ProfileId is not { } profileId
            || string.IsNullOrWhiteSpace(recovery.TunnelName)
            || !RouterOSPeerCreation.IsWireGuardKey(recovery.OriginalPeerPublicKey ?? string.Empty)
            || !RouterOSPeerCreation.IsWireGuardKey(recovery.ReplacementPublicKey ?? string.Empty))
        {
            return;
        }

        var originalPeerPublicKey = recovery.OriginalPeerPublicKey!;
        var replacementPublicKey = recovery.ReplacementPublicKey!;

        var peer = routerOSPeers.FirstOrDefault(value =>
            value.Id.Equals(selectedPeer.Id, StringComparison.Ordinal)) ?? selectedPeer;
        var keyState = RouterOSPeerKeyRecoveryReconciler.Reconcile(
            peer.PublicKey,
            originalPeerPublicKey,
            replacementPublicKey);
        if (keyState == RouterOSPeerKeyRecoveryState.Conflict)
        {
            await ShowConfigurationRecoveryAsync(
                recovery,
                "RouterOS peer key changed",
                "The selected peer no longer matches the original or replacement recovery key. WireRoute made no changes.");
            return;
        }

        SetRouterOSBusy(true);
        var confirmedPeer = peer;
        try
        {
            if (keyState == RouterOSPeerKeyRecoveryState.RequiresReplacement)
            {
                SetRouterOSStatus("Replacing the reviewed RouterOS peer public key…");
                try
                {
                    using var transport = new RouterOSHttpTransport(url, context.TrustedCertificate);
                    var client = new RouterOSClient(
                        url,
                        new RouterOSCredentials(context.Connection.Username, context.Connection.Password),
                        transport);
                    confirmedPeer = await client.ReplaceWireGuardPeerPublicKeyAsync(
                        peer,
                        replacementPublicKey);
                    if (!confirmedPeer.Id.Equals(peer.Id, StringComparison.Ordinal)
                        || !confirmedPeer.PublicKey.Equals(
                            replacementPublicKey,
                            StringComparison.Ordinal))
                    {
                        throw new RouterOSWriteOutcomeUncertainException(
                            new InvalidDataException(
                                "RouterOS did not confirm the selected peer and replacement key."));
                    }
                }
                catch (RouterOSHttpException exception) when (
                    (int)exception.StatusCode is >= 400 and < 500
                    && exception.StatusCode != System.Net.HttpStatusCode.RequestTimeout)
                {
                    var cleanupMessage = await TryDeleteProfileRecoveryAsync(recovery.Id);
                    SetRouterOSStatus($"{exception.Message}{cleanupMessage}", isError: true);
                    return;
                }
                catch (Exception exception)
                {
                    var uncertain = recovery with
                    {
                        Reason = RouterOSProfileRecoveryReason.RouterKeyReplacementUncertain,
                    };
                    var storageMessage = await TrySaveProfileRecoveryAsync(uncertain);
                    InvalidateRouterOSDiscovery(exception.Message);
                    await ShowConfigurationRecoveryAsync(
                        uncertain,
                        "RouterOS key replacement needs verification",
                        $"The peer may already have the replacement key. Reconnect and select the same peer to resume safely; do not start a second recovery. The private configuration remains protected.{storageMessage}");
                    return;
                }

                routerOSPeers = routerOSPeers
                    .Select(value => value.Id.Equals(confirmedPeer.Id, StringComparison.Ordinal)
                        ? confirmedPeer
                        : value)
                    .ToArray();
                RebuildRouterOSDiscovery();
            }

            SetRouterOSStatus("RouterOS confirmed the replacement key. Importing the matching private profile…");
            try
            {
                _ = await ImportGeneratedProfileAsync(
                    recovery.DisplayName,
                    recovery.WgQuickConfiguration,
                    profileId,
                    recovery.TunnelName);
                var cleanupMessage = await TryDeleteProfileRecoveryAsync(recovery.Id);
                SetRouterOSStatus(
                    $"Profile recovered and matched to the RouterOS peer.{cleanupMessage}",
                    isSuccess: cleanupMessage.Length == 0);
                if (cleanupMessage.Length > 0)
                {
                    await ShowMessageAsync(
                        "Profile recovered with a cleanup notice",
                        $"The peer and local profile are ready, but the protected recovery copy could not be cleared. {cleanupMessage}");
                }
            }
            catch (Exception exception)
            {
                var importRecovery = recovery with
                {
                    Reason = RouterOSProfileRecoveryReason.ManagerImportFailed,
                };
                var storageMessage = await TrySaveProfileRecoveryAsync(importRecovery);
                await ShowConfigurationRecoveryAsync(
                    importRecovery,
                    "RouterOS updated; profile import needs attention",
                    $"RouterOS confirmed the replacement key, but WireRoute could not import its private profile. Select this peer and Resume Recovery after the import issue is resolved. {exception.Message}{storageMessage}");
            }
        }
        finally
        {
            SetRouterOSBusy(false);
        }
    }

    private async Task CreateRouterOSPeerAndImportAsync(RouterOSPeerSetupProposal proposal)
    {
        var peerCreation = proposal.PeerCreation;
        var context = routerOSConnectedContext;
        if (peerCreation is null
            || context is null
            || !Uri.TryCreate(context.Connection.Url, UriKind.Absolute, out var url))
        {
            return;
        }

        var recovery = new RouterOSProfileRecovery(
            Guid.NewGuid(),
            proposal.ClientConfiguration.Name,
            proposal.ClientConfiguration.WgQuickConfiguration,
            DateTimeOffset.UtcNow,
            RouterOSProfileRecoveryReason.PendingRouterWrite);
        try
        {
            await routerOSProfileRecoveryStore.SaveAsync(recovery);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Peer setup was stopped safely",
                $"The recovery configuration could not be protected before changing RouterOS. No router changes were made.\n\n{exception.Message}");
            return;
        }

        SetRouterOSBusy(true);
        SetRouterOSStatus("Adding the reviewed peer to RouterOS…");
        try
        {
            using var transport = new RouterOSHttpTransport(url, context.TrustedCertificate);
            var client = new RouterOSClient(
                url,
                new RouterOSCredentials(context.Connection.Username, context.Connection.Password),
                transport);
            var createdPeer = await client.CreateWireGuardPeerAsync(peerCreation);
            routerOSPeers = routerOSPeers.Append(createdPeer).ToArray();
            RebuildRouterOSDiscovery();
            SetRouterOSStatus("RouterOS confirmed the peer. Importing its matching profile…");
            try
            {
                _ = await ImportGeneratedProfileAsync(
                    proposal.ClientConfiguration.Name,
                    proposal.ClientConfiguration.WgQuickConfiguration,
                    proposal.ProfileId,
                    proposal.TunnelName);
                var cleanupMessage = await TryDeleteProfileRecoveryAsync(recovery.Id);
                SetRouterOSStatus("Peer created and profile loaded into WireRoute.", isSuccess: true);
                if (cleanupMessage.Length > 0)
                {
                    await ShowMessageAsync(
                        "Profile loaded with a recovery notice",
                        $"The peer and local profile are ready, but a protected recovery copy could not be cleared. {cleanupMessage}");
                }
            }
            catch (Exception exception)
            {
                recovery = recovery with { Reason = RouterOSProfileRecoveryReason.ManagerImportFailed };
                var storageMessage = await TrySaveProfileRecoveryAsync(recovery);
                await ShowConfigurationRecoveryAsync(
                    recovery,
                    "Peer created; profile import needs attention",
                    $"RouterOS confirmed the peer, but WireRoute could not import its local profile. {exception.Message}{storageMessage}");
            }
        }
        catch (RouterOSHttpException exception) when (
            (int)exception.StatusCode is >= 400 and < 500
            && exception.StatusCode != System.Net.HttpStatusCode.RequestTimeout)
        {
            var cleanupMessage = await TryDeleteProfileRecoveryAsync(recovery.Id);
            SetRouterOSStatus($"{exception.Message}{cleanupMessage}", isError: true);
        }
        catch (RouterOSWriteOutcomeUncertainException exception)
        {
            recovery = recovery with { Reason = RouterOSProfileRecoveryReason.RouterWriteUncertain };
            var storageMessage = await TrySaveProfileRecoveryAsync(recovery);
            InvalidateRouterOSDiscovery(exception.Message);
            await ShowConfigurationRecoveryAsync(
                recovery,
                "RouterOS write outcome is uncertain",
                $"The peer may already exist. Reconnect and verify RouterOS before retrying. The matching private configuration has been retained securely.{storageMessage}");
        }
        catch (Exception exception)
        {
            recovery = recovery with { Reason = RouterOSProfileRecoveryReason.RouterWriteUncertain };
            var storageMessage = await TrySaveProfileRecoveryAsync(recovery);
            InvalidateRouterOSDiscovery(exception.Message);
            await ShowConfigurationRecoveryAsync(
                recovery,
                "Peer setup needs verification",
                $"Reconnect and verify whether RouterOS created the peer before retrying. The matching private configuration has been retained securely.{storageMessage}");
        }
        finally
        {
            SetRouterOSBusy(false);
        }
    }

    private async Task<string> TrySaveProfileRecoveryAsync(RouterOSProfileRecovery recovery)
    {
        try
        {
            await routerOSProfileRecoveryStore.SaveAsync(recovery);
            await RefreshRouterOSProfileRecoveriesAsync();
            return string.Empty;
        }
        catch (Exception exception)
        {
            return $"\n\nProtected recovery storage also reported: {exception.Message}";
        }
    }

    private async Task<string> TryDeleteProfileRecoveryAsync(Guid id)
    {
        try
        {
            await routerOSProfileRecoveryStore.DeleteAsync(id);
            await RefreshRouterOSProfileRecoveriesAsync();
            return string.Empty;
        }
        catch (Exception exception)
        {
            return $" Protected recovery cleanup reported: {exception.Message}";
        }
    }

    private async Task ShowConfigurationRecoveryAsync(
        RouterOSProfileRecovery recovery,
        string title,
        string message)
    {
        var content = new StackPanel { MaxWidth = 590, Spacing = 10 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            Text = "The configuration contains a private key. Save or copy it only to a trusted location.",
            TextWrapping = TextWrapping.Wrap,
        });
        var result = await ShowModalAsync(new ModalRequest
        {
            Title = title,
            Content = ModalCard(content),
            PrimaryText = "Save Configuration…",
            SecondaryText = "Copy Configuration",
            CancelText = "Done",
            MaxWidth = 720,
        });
        if (result == WireRouteModalResult.Primary)
        {
            await SaveRecoveryConfigurationAsync(recovery);
        }
        else if (result == WireRouteModalResult.Secondary)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(recovery.WgQuickConfiguration);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                await ShowMessageAsync("Configuration copied", "The sensitive WireGuard configuration is on the clipboard.");
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("Configuration could not be copied", exception.Message);
            }
        }
    }

    private async Task SaveRecoveryConfigurationAsync(RouterOSProfileRecovery recovery)
    {
        try
        {
            var picker = new FileSavePicker
            {
                CommitButtonText = "Save Configuration",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = SafeConfigurationFileName(recovery.DisplayName),
            };
            picker.FileTypeChoices.Add("WireGuard configuration", [".conf"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                await FileIO.WriteTextAsync(file, recovery.WgQuickConfiguration);
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Configuration could not be saved", exception.Message);
        }
    }

    private static TextBlock SetupHelpText(string value) => new()
    {
        Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
        FontSize = 11,
        Text = value,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Grid ReviewDetail(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelText = SetupHelpText(label);
        var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(labelText);
        grid.Children.Add(valueText);
        return grid;
    }

    private static string[] SplitSetupValues(string value) => value
        .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SafeConfigurationFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var rendered = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return rendered.Length == 0 ? "WireRoute" : rendered;
    }

    private static string? FirstNonempty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));

    private sealed class RouterOSPeerSetupState
    {
        public string InterfaceName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string ClientAddress { get; set; } = string.Empty;

        public string? LastSuggestedClientAddress { get; set; }

        public string EndpointAddress { get; set; } = string.Empty;

        public string EndpointPort { get; set; } = string.Empty;

        public string? LastSuggestedEndpointPort { get; set; }

        public string DnsServers { get; set; } = string.Empty;

        public int RouteModeIndex { get; set; }

        public string Routes { get; set; } = string.Empty;

        public string PersistentKeepalive { get; set; } = "25";
    }

    private sealed record RouterOSPeerSetupProposal(
        RouterOSPeerCreation? PeerCreation,
        RouterOSWireGuardPeer? RecoveryPeer,
        string ClientPublicKey,
        WireGuardClientConfiguration ClientConfiguration,
        Guid ProfileId,
        string TunnelName);
}
