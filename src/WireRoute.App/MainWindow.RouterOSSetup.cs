using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;
using WireRoute.RouterOS;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private readonly RouterOSProfileRecoveryStore routerOSProfileRecoveryStore = new();

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

    private async Task<RouterOSPeerSetupProposal?> ShowRouterOSPeerSetupAsync(
        RouterOSPeerSetupState state,
        WireGuardKeyPair keyPair)
    {
        var interfacePicker = new ComboBox
        {
            DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = routerOSInterfaces,
        };
        interfacePicker.SelectedItem = routerOSInterfaces.FirstOrDefault(value =>
            value.Name.Equals(state.InterfaceName, StringComparison.Ordinal));
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
                var suggestion = RouterOSClientAddressSuggestion.Discover(selected.Name, routerOSPeers);
                state.LastSuggestedClientAddress = suggestion?.Address.Notation;
                addressField.Text = suggestion?.Address.Notation ?? string.Empty;
                addressHelp.Text = suggestion is null
                    ? $"No unambiguous /32 could be suggested for {selected.Name}. Enter the client address manually."
                    : $"Suggested from existing /32 peers on {selected.Name}. Verify it before continuing.";
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
            Text = "WireRoute generated this device’s private key locally. It will never be sent to RouterOS.",
            TextWrapping = TextWrapping.Wrap,
        });
        form.Children.Add(formGrid);
        form.Children.Add(errorText);
        RouterOSPeerSetupProposal? proposal = null;
        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = "Set Up a Device",
            Subtitle = "WireRoute generates a fresh key pair locally, suggests safe values from RouterOS, prefills your defaults, and lets you review the exact change.",
            Content = ModalCard(form),
            PrimaryText = "Review Peer",
            CancelText = "Cancel",
            MaxWidth = 960,
            OnPrimary = () =>
            {
                try
                {
                    proposal = MakeRouterOSPeerProposal(
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
            clientConfiguration,
            profileId,
            tunnelName);
    }

    private Task<WireRouteModalResult> ShowRouterOSPeerReviewAsync(RouterOSPeerSetupProposal proposal)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "WireRoute will add exactly one WireGuard peer. It will not change RouterOS addresses, routes, firewall rules, or NAT.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(ReviewDetail("Device", proposal.PeerCreation.Name));
        content.Children.Add(ReviewDetail("Interface", proposal.PeerCreation.InterfaceName));
        content.Children.Add(ReviewDetail("Client address", proposal.PeerCreation.ClientAddress.Notation));
        content.Children.Add(ReviewDetail(
            "Endpoint",
            $"{proposal.ClientConfiguration.EndpointAddress}:{proposal.ClientConfiguration.EndpointPort}"));
        content.Children.Add(ReviewDetail(
            "Client routes",
            string.Join(", ", proposal.ClientConfiguration.AllowedIps.Select(value => value.Notation))));
        return ShowModalAsync(new ModalRequest
        {
            Title = "Review RouterOS Peer",
            Subtitle = "Confirm the exact RouterOS change and matching local WireGuard profile.",
            Content = ModalCard(content),
            PrimaryText = "Add Peer",
            SecondaryText = "Back",
            CancelText = "Cancel",
            MaxWidth = 760,
        });
    }

    private async Task CreateRouterOSPeerAndImportAsync(RouterOSPeerSetupProposal proposal)
    {
        var context = routerOSConnectedContext;
        if (context is null || !Uri.TryCreate(context.Connection.Url, UriKind.Absolute, out var url))
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
            var createdPeer = await client.CreateWireGuardPeerAsync(proposal.PeerCreation);
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
        RouterOSPeerCreation PeerCreation,
        WireGuardClientConfiguration ClientConfiguration,
        Guid ProfileId,
        string TunnelName);
}
