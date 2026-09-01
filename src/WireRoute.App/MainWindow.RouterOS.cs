using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WireRoute.App.Models;
using WireRoute.RouterOS;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private readonly RouterOSConnectionStore routerOSConnectionStore = new();
    private readonly RouterOSCertificateStore routerOSCertificateStore = new();
    private IReadOnlyList<RouterOSWireGuardInterface> routerOSInterfaces = [];
    private IReadOnlyList<RouterOSWireGuardPeer> routerOSPeers = [];
    private RouterOSPublicEndpointSuggestion? routerOSPublicEndpointSuggestion;
    private RouterOSConnectedContext? routerOSConnectedContext;
    private bool isLoadingRouterOSConnections;
    private bool isRouterOSBusy;

    public ObservableCollection<RouterOSConnectionRow> RouterOSConnections { get; } = [];

    public ObservableCollection<RouterOSDiscoveryRow> RouterOSDiscoveryRows { get; } = [];

    private async Task LoadRouterOSConnectionsAsync(Guid? preferredId = null)
    {
        try
        {
            isLoadingRouterOSConnections = true;
            preferredId ??= (RouterOSConnectionPicker.SelectedItem as RouterOSConnectionRow)?.Id;
            var connections = (await routerOSConnectionStore.LoadAllAsync())
                .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            RouterOSConnections.Clear();
            foreach (var connection in connections.Select(value => new RouterOSConnectionRow(value)))
            {
                RouterOSConnections.Add(connection);
            }

            var selected = preferredId is null
                ? RouterOSConnections.FirstOrDefault()
                : RouterOSConnections.FirstOrDefault(value => value.Id == preferredId)
                    ?? RouterOSConnections.FirstOrDefault();
            RouterOSConnectionPicker.SelectedItem = selected;
            RouterOSConnectionsList.SelectedItem = selected;
            RouterOSConnectionsEmptyText.Visibility = connections.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            RouterOSConnectButton.IsEnabled = connections.Length > 0 && !isRouterOSBusy;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("RouterOS connections are unavailable", exception.Message);
        }
        finally
        {
            isLoadingRouterOSConnections = false;
            UpdateRouterOSConnectionButtons();
        }
    }

    private void RouterOSConnectionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isLoadingRouterOSConnections)
        {
            return;
        }

        var selected = RouterOSConnectionPicker.SelectedItem as RouterOSConnectionRow;
        RouterOSConnectionsList.SelectedItem = selected;
        if (routerOSConnectedContext is not null && routerOSConnectedContext.Connection.Id != selected?.Id)
        {
            InvalidateRouterOSDiscovery("Connection details changed. Connect again to refresh discovery.");
        }

        RouterOSConnectButton.IsEnabled = selected is not null && !isRouterOSBusy;
    }

    private void RouterOSConnectionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRouterOSConnectionButtons();

    private void UpdateRouterOSConnectionButtons()
    {
        var hasSelection = RouterOSConnectionsList.SelectedItem is RouterOSConnectionRow;
        RouterOSEditConnectionButton.IsEnabled = hasSelection;
        RouterOSRemoveConnectionButton.IsEnabled = hasSelection;
    }

    private async void RouterOSConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (RouterOSConnectionPicker.SelectedItem is not RouterOSConnectionRow connectionRow)
        {
            await ShowMessageAsync(
                "No RouterOS connection",
                "No RouterOS connections are set up. Open Settings to add one.");
            return;
        }

        await ConnectRouterOSAsync(connectionRow.Connection, allowCertificateReview: true);
    }

    private async Task ConnectRouterOSAsync(
        RouterOSStoredConnection connection,
        bool allowCertificateReview)
    {
        if (!Uri.TryCreate(connection.Url, UriKind.Absolute, out var url))
        {
            SetRouterOSStatus("Enter a valid RouterOS address in Settings.", isError: true);
            return;
        }

        SetRouterOSBusy(true);
        SetRouterOSStatus("Connecting securely…");
        InvalidateRouterOSDiscovery(status: null);
        try
        {
            var trustedCertificate = await routerOSCertificateStore.LoadAsync(url);
            using var transport = new RouterOSHttpTransport(url, trustedCertificate);
            var client = new RouterOSClient(
                url,
                new RouterOSCredentials(connection.Username, connection.Password),
                transport);
            var interfacesTask = client.GetWireGuardInterfacesAsync();
            var peersTask = client.GetWireGuardPeersAsync();
            var addressesTask = GetOptionalRouterOSAddressesAsync(client);
            await Task.WhenAll(interfacesTask, peersTask, addressesTask);

            routerOSInterfaces = await interfacesTask;
            routerOSPeers = await peersTask;
            var addresses = await addressesTask;
            routerOSPublicEndpointSuggestion = RouterOSPublicEndpointSuggestion.Discover(addresses);
            routerOSConnectedContext = new RouterOSConnectedContext(connection, trustedCertificate);
            await RefreshRouterOSProfileRecoveriesAsync();
            RebuildRouterOSDiscovery();
            RouterOSConnectButton.Content = "Connected";
            ToolTipService.SetToolTip(
                RouterOSConnectButton,
                "Connected to this router. Click to refresh discovery.");
            RouterOSShowAllPeersCheckBox.IsEnabled = routerOSPeers.Count > 0;
            UpdateRouterOSManagerAvailability();
            SetRouterOSStatus("Discovery completed securely with no RouterOS changes.", isSuccess: true);
        }
        catch (RouterOSTlsCertificateException exception) when (allowCertificateReview)
        {
            await PresentRouterOSCertificateReviewAsync(connection, url, exception);
        }
        catch (OperationCanceledException)
        {
            SetRouterOSStatus("RouterOS connection cancelled.");
        }
        catch (Exception exception)
        {
            SetRouterOSStatus(exception.Message, isError: true);
        }
        finally
        {
            SetRouterOSBusy(false);
        }
    }

    private static async Task<IReadOnlyList<RouterOSIpAddress>> GetOptionalRouterOSAddressesAsync(
        RouterOSClient client)
    {
        try
        {
            return await client.GetIpAddressesAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task PresentRouterOSCertificateReviewAsync(
        RouterOSStoredConnection connection,
        Uri url,
        RouterOSTlsCertificateException exception)
    {
        var certificate = exception.ReceivedCertificate;
        var isReplacement = exception is RouterOSChangedCertificateException;
        var content = new StackPanel { MaxWidth = 620, Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = isReplacement
                ? "The certificate no longer matches the one you trusted. This can be normal after a renewal, but it can also indicate an unexpected router or intercepted connection. Verify both fingerprints before replacing it."
                : "This router uses a certificate Windows does not recognize. Compare the SHA-256 fingerprint with the certificate on RouterOS before trusting it.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(CertificateDetail("Router", $"{certificate.Host}:{certificate.Port}"));
        content.Children.Add(CertificateDetail("Certificate", certificate.SubjectSummary ?? "Unnamed certificate"));
        if (exception is RouterOSChangedCertificateException changed)
        {
            content.Children.Add(CertificateDetail("Previously trusted", changed.ExpectedFingerprint));
        }

        content.Children.Add(CertificateDetail("Presented now", certificate.FingerprintSha256));

        var result = await ShowModalAsync(new ModalRequest
        {
            Title = isReplacement ? "Router Certificate Changed" : "Verify Router Certificate",
            Content = ModalCard(content),
            PrimaryText = isReplacement ? "Replace Trust and Connect" : "Trust and Connect",
            CancelText = "Cancel",
            MaxWidth = 760,
        });
        if (result != WireRouteModalResult.Primary)
        {
            SetRouterOSStatus(
                "Connection cancelled. The router certificate was not trusted.");
            return;
        }

        try
        {
            await routerOSCertificateStore.SaveAsync(certificate);
            SetRouterOSStatus("Certificate trusted for this router. Connecting securely…");
            await ConnectRouterOSAsync(connection, allowCertificateReview: false);
        }
        catch (Exception storageException)
        {
            SetRouterOSStatus(storageException.Message, isError: true);
        }
    }

    private static Grid CertificateDetail(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelText = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            Text = label,
        };
        var valueText = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono"),
            Text = value,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(labelText);
        grid.Children.Add(valueText);
        return grid;
    }

    private void SetRouterOSBusy(bool isBusy)
    {
        isRouterOSBusy = isBusy;
        RouterOSProgressRing.IsActive = isBusy;
        RouterOSConnectionPicker.IsEnabled = !isBusy && RouterOSConnections.Count > 0;
        RouterOSConnectButton.IsEnabled = !isBusy && RouterOSConnectionPicker.SelectedItem is not null;
        RouterOSShowAllPeersCheckBox.IsEnabled = !isBusy && routerOSPeers.Count > 0;
        UpdateRouterOSManagerAvailability();
        UpdateRouterOSRecoveryAction();
    }

    private void SetRouterOSStatus(string status, bool isError = false, bool isSuccess = false)
    {
        RouterOSStatusText.Text = status;
        RouterOSStatusText.Foreground = isError
            ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
            : isSuccess
                ? new SolidColorBrush(Microsoft.UI.Colors.LightGreen)
                : (Brush)Application.Current.Resources["NordicSecondaryTextBrush"];
    }

    private void InvalidateRouterOSDiscovery(string? status)
    {
        routerOSConnectedContext = null;
        routerOSInterfaces = [];
        routerOSPeers = [];
        routerOSPublicEndpointSuggestion = null;
        RouterOSDiscoveryRows.Clear();
        RouterOSDiscoveryList.SelectedItem = null;
        RouterOSSummaryText.Text = "Not connected";
        RouterOSDiscoveryEmptyText.Text = "Connect to securely discover WireGuard peers.";
        RouterOSDiscoveryEmptyText.Visibility = Visibility.Visible;
        RouterOSShowAllPeersCheckBox.IsChecked = false;
        RouterOSShowAllPeersCheckBox.IsEnabled = false;
        RouterOSSetUpPeerButton.IsEnabled = false;
        RouterOSRecoverProfileButton.IsEnabled = false;
        RouterOSConnectButton.Content = "Connect";
        ToolTipService.SetToolTip(RouterOSConnectButton, null);
        if (status is not null)
        {
            SetRouterOSStatus(status);
        }
    }

    private void RebuildRouterOSDiscovery()
    {
        var selectedPeerId = (RouterOSDiscoveryList.SelectedItem as RouterOSDiscoveryRow)?.Peer?.Id;
        var managedPeers = routerOSPeers
            .Where(peer => RouterOSPeerCreation.IsWireRouteManagedComment(peer.Comment))
            .ToArray();
        var displayedPeers = RouterOSShowAllPeersCheckBox.IsChecked == true
            ? routerOSPeers
            : managedPeers;
        RouterOSDiscoveryRows.Clear();
        foreach (var peer in displayedPeers)
        {
            RouterOSDiscoveryRows.Add(RouterOSDiscoveryRow.FromPeer(peer));
        }

        RouterOSDiscoveryList.SelectedItem = RouterOSDiscoveryRows.FirstOrDefault(value =>
            value.Peer?.Id.Equals(selectedPeerId, StringComparison.Ordinal) == true);

        RouterOSSummaryText.Text = RouterOSShowAllPeersCheckBox.IsChecked == true
            ? $"{routerOSPeers.Count} peers"
            : $"{managedPeers.Length} WireRoute clients • {routerOSPeers.Count} total peers";
        RouterOSDiscoveryEmptyText.Text = routerOSPeers.Count == 0
            ? "No WireGuard peers were found on this router."
            : managedPeers.Length == 0 && RouterOSShowAllPeersCheckBox.IsChecked != true
                ? "No WireRoute-managed clients were found. Select Show all peers to view site-to-site or manually created peers."
                : string.Empty;
        RouterOSDiscoveryEmptyText.Visibility = RouterOSDiscoveryRows.Count == 0
            || (routerOSPeers.Count > 0 && displayedPeers.Count == 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateRouterOSRecoveryAction();
    }

    private void RouterOSDiscoveryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRouterOSRecoveryAction();

    private void RouterOSShowAllPeersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (routerOSConnectedContext is not null)
        {
            RebuildRouterOSDiscovery();
        }
    }

    private void RouterOSManageConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowDestination(Destination.Settings);
        RouterOSConnectionsList.SelectedItem = RouterOSConnectionPicker.SelectedItem;
    }

    private async void RouterOSAddConnectionButton_Click(object sender, RoutedEventArgs e) =>
        await ShowRouterOSConnectionEditorAsync(existing: null);

    private async void RouterOSEditConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (RouterOSConnectionsList.SelectedItem is RouterOSConnectionRow connection)
        {
            await ShowRouterOSConnectionEditorAsync(connection.Connection);
        }
    }

    private async Task ShowRouterOSConnectionEditorAsync(RouterOSStoredConnection? existing)
    {
        var insetBrush = (Brush)Application.Current.Resources["NordicInsetBrush"];
        var surfaceBrush = (Brush)Application.Current.Resources["NordicSurfaceBrush"];
        var raisedBrush = (Brush)Application.Current.Resources["NordicRaisedBrush"];
        var borderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"];
        var secondaryTextBrush = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"];
        var compactInterfaceRow = Root.ActualWidth > 0 && Root.ActualWidth < 760;

        var nameField = new TextBox
        {
            Background = insetBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40,
            PlaceholderText = "Home Router",
            Text = existing?.Name ?? string.Empty,
        };
        var addressField = new TextBox
        {
            Background = insetBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40,
            PlaceholderText = "https://router.example",
            Text = existing?.Url ?? string.Empty,
        };
        var usernameField = new TextBox
        {
            Background = insetBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40,
            PlaceholderText = "RouterOS API user",
            Text = existing?.Username ?? string.Empty,
        };
        var passwordField = new PasswordBox
        {
            Background = insetBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40,
            PlaceholderText = existing is null
                ? "Required; protected with Windows DPAPI"
                : "Leave blank to keep the saved password",
        };
        var interfaceChoices = new ObservableCollection<RouterOSInterfaceChoice>();
        var automaticInterface = new RouterOSInterfaceChoice("Automatic", null);
        interfaceChoices.Add(automaticInterface);
        RouterOSInterfaceChoice initialInterface = automaticInterface;
        if (!string.IsNullOrWhiteSpace(existing?.DefaultInterface))
        {
            initialInterface = new RouterOSInterfaceChoice(existing.DefaultInterface, existing.DefaultInterface);
            interfaceChoices.Add(initialInterface);
        }

        var defaultInterfacePicker = new ComboBox
        {
            Background = insetBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ItemsSource = interfaceChoices,
            MinHeight = 40,
            SelectedItem = initialInterface,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var loadInterfacesButton = new Button
        {
            Content = "Connect and Load Interfaces",
            Background = raisedBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinHeight = 40,
            Padding = new Thickness(14, 8, 14, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var loadInterfacesProgress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var interfaceGrid = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        interfaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        interfaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (compactInterfaceRow)
        {
            interfaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            interfaceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumnSpan(defaultInterfacePicker, 2);
            Grid.SetRow(loadInterfacesButton, 1);
            Grid.SetRow(loadInterfacesProgress, 1);
            Grid.SetColumn(loadInterfacesProgress, 1);
        }
        else
        {
            interfaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(loadInterfacesButton, 1);
            Grid.SetColumn(loadInterfacesProgress, 2);
        }

        interfaceGrid.Children.Add(defaultInterfacePicker);
        interfaceGrid.Children.Add(loadInterfacesButton);
        interfaceGrid.Children.Add(loadInterfacesProgress);

        var interfaceHelpText = new TextBlock
        {
            Foreground = secondaryTextBrush,
            FontSize = 12,
            Text = "Connects read-only to list this router's WireGuard interfaces. No RouterOS settings are changed.",
            TextWrapping = TextWrapping.Wrap,
        };
        var defaultInterfaceStack = new StackPanel { Spacing = 5 };
        defaultInterfaceStack.Children.Add(interfaceGrid);
        defaultInterfaceStack.Children.Add(interfaceHelpText);

        var statusText = new TextBlock
        {
            Foreground = secondaryTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var certificateReview = new StackPanel
        {
            Spacing = 10,
            Visibility = Visibility.Collapsed,
        };

        var formGrid = new Grid { ColumnSpacing = 14, RowSpacing = 12 };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(compactInterfaceRow ? 118 : 150),
        });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < 5; row++)
        {
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        void AddFormRow(int row, string label, FrameworkElement field)
        {
            var labelText = new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = label,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(labelText, row);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            formGrid.Children.Add(labelText);
            formGrid.Children.Add(field);
        }

        AddFormRow(0, "Name", nameField);
        AddFormRow(1, "Router address", addressField);
        AddFormRow(2, "Username", usernameField);
        AddFormRow(3, "Password", passwordField);
        AddFormRow(4, "Default interface", defaultInterfaceStack);

        var formCard = new Border
        {
            Background = surfaceBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Child = formGrid,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(formCard);
        content.Children.Add(statusText);
        content.Children.Add(certificateReview);
        RouterOSStoredConnection? saved = null;
        ModalRequest? modalRequest = null;

        RouterOSStoredConnection ReadConnectionFields()
        {
            var name = nameField.Text.Trim();
            var address = addressField.Text.Trim();
            var username = usernameField.Text.Trim();
            var password = passwordField.Password.Length == 0
                ? existing?.Password ?? string.Empty
                : passwordField.Password;
            if (name.Length == 0)
            {
                throw new ArgumentException("Enter a name for this connection.");
            }

            if (!Uri.TryCreate(address, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrWhiteSpace(url.Host))
            {
                throw new ArgumentException(
                    "Enter a complete secure RouterOS address beginning with https://.");
            }

            if (username.Length == 0)
            {
                throw new ArgumentException("Enter the RouterOS username.");
            }

            if (password.Length == 0)
            {
                throw new ArgumentException("Enter the RouterOS password.");
            }

            var selectedInterface = defaultInterfacePicker.SelectedItem as RouterOSInterfaceChoice;
            return new RouterOSStoredConnection(
                existing?.Id ?? Guid.NewGuid(),
                name,
                url.AbsoluteUri,
                username,
                password,
                selectedInterface?.InterfaceName);
        }

        void SetStatus(string message, bool isError = false, bool isSuccess = false)
        {
            statusText.Text = message;
            statusText.Foreground = isError
                ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
                : isSuccess
                    ? new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen)
                    : (Brush)Application.Current.Resources["NordicSecondaryTextBrush"];
            statusText.Visibility = Visibility.Visible;
        }

        var isReviewingCertificate = false;
        void SetEditorBusy(bool busy)
        {
            var enableFields = !busy && !isReviewingCertificate;
            nameField.IsEnabled = enableFields;
            addressField.IsEnabled = enableFields;
            usernameField.IsEnabled = enableFields;
            passwordField.IsEnabled = enableFields;
            defaultInterfacePicker.IsEnabled = enableFields;
            loadInterfacesButton.IsEnabled = enableFields;
            modalRequest?.SetPrimaryEnabled(enableFields);
            modalRequest?.SetCancelEnabled(!busy);
            loadInterfacesProgress.IsActive = busy;
            loadInterfacesProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        void PopulateInterfaces(IReadOnlyList<RouterOSWireGuardInterface> interfaces)
        {
            var previous = (defaultInterfacePicker.SelectedItem as RouterOSInterfaceChoice)?.InterfaceName;
            interfaceChoices.Clear();
            interfaceChoices.Add(automaticInterface);
            foreach (var item in interfaces)
            {
                interfaceChoices.Add(new RouterOSInterfaceChoice(
                    item.IsDisabled ? $"{item.Name} — Disabled" : item.Name,
                    item.Name));
            }

            var selected = previous is null
                ? null
                : interfaceChoices.FirstOrDefault(choice =>
                    choice.InterfaceName?.Equals(previous, StringComparison.Ordinal) == true);
            selected ??= interfaces
                .Where(item => item.IsRunning && !item.IsDisabled)
                .Select(item => interfaceChoices.First(choice => choice.InterfaceName == item.Name))
                .FirstOrDefault();
            selected ??= interfaces
                .Where(item => !item.IsDisabled)
                .Select(item => interfaceChoices.First(choice => choice.InterfaceName == item.Name))
                .FirstOrDefault();
            selected ??= interfaceChoices.Skip(1).FirstOrDefault();
            defaultInterfacePicker.SelectedItem = selected ?? automaticInterface;
        }

        async Task LoadInterfacesAsync(bool allowCertificateReview)
        {
            RouterOSStoredConnection connection;
            Uri url;
            try
            {
                connection = ReadConnectionFields();
                url = new Uri(connection.Url);
            }
            catch (Exception exception)
            {
                SetStatus(exception.Message, isError: true);
                return;
            }

            SetEditorBusy(true);
            SetStatus("Connecting securely and loading WireGuard interfaces…");
            try
            {
                var trustedCertificate = await routerOSCertificateStore.LoadAsync(url);
                using var transport = new RouterOSHttpTransport(url, trustedCertificate);
                var client = new RouterOSClient(
                    url,
                    new RouterOSCredentials(connection.Username, connection.Password),
                    transport);
                var interfaces = await client.GetWireGuardInterfacesAsync();
                PopulateInterfaces(interfaces);
                SetStatus(
                    interfaces.Count == 0
                        ? "No WireGuard interfaces were found on this router."
                        : interfaces.Count == 1
                            ? "1 interface loaded."
                            : $"{interfaces.Count} interfaces loaded.",
                    isSuccess: interfaces.Count > 0);
            }
            catch (RouterOSTlsCertificateException exception) when (allowCertificateReview)
            {
                var certificate = exception.ReceivedCertificate;
                var isReplacement = exception is RouterOSChangedCertificateException;
                isReviewingCertificate = true;
                certificateReview.Children.Clear();
                certificateReview.Children.Add(new TextBlock
                {
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Text = isReplacement ? "Router Certificate Changed" : "Verify Router Certificate",
                });
                certificateReview.Children.Add(new TextBlock
                {
                    Text = isReplacement
                        ? "The certificate no longer matches the one you trusted. Verify both fingerprints before replacing it."
                        : "This router uses a certificate Windows does not recognize. Compare the SHA-256 fingerprint with the certificate on RouterOS before trusting it.",
                    TextWrapping = TextWrapping.Wrap,
                });
                certificateReview.Children.Add(CertificateDetail("Router", $"{certificate.Host}:{certificate.Port}"));
                certificateReview.Children.Add(CertificateDetail("Certificate", certificate.SubjectSummary ?? "Unnamed certificate"));
                if (exception is RouterOSChangedCertificateException changed)
                {
                    certificateReview.Children.Add(CertificateDetail("Previously trusted", changed.ExpectedFingerprint));
                }

                certificateReview.Children.Add(CertificateDetail("Presented now", certificate.FingerprintSha256));
                var trustButton = new Button
                {
                    Content = isReplacement ? "Replace Trust and Load" : "Trust and Load",
                    Style = (Style)Application.Current.Resources["NordicAccentButtonStyle"],
                };
                var cancelTrustButton = new Button { Content = "Cancel" };
                var reviewButtons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                };
                reviewButtons.Children.Add(trustButton);
                reviewButtons.Children.Add(cancelTrustButton);
                certificateReview.Children.Add(reviewButtons);
                certificateReview.Visibility = Visibility.Visible;
                SetStatus("Verify the router certificate before loading interfaces.");
                trustButton.Click += async (_, _) =>
                {
                    try
                    {
                        trustButton.IsEnabled = false;
                        cancelTrustButton.IsEnabled = false;
                        await routerOSCertificateStore.SaveAsync(certificate);
                        isReviewingCertificate = false;
                        certificateReview.Visibility = Visibility.Collapsed;
                        SetEditorBusy(false);
                        await LoadInterfacesAsync(allowCertificateReview: false);
                    }
                    catch (Exception storageException)
                    {
                        trustButton.IsEnabled = true;
                        cancelTrustButton.IsEnabled = true;
                        SetStatus(storageException.Message, isError: true);
                    }
                };
                cancelTrustButton.Click += (_, _) =>
                {
                    isReviewingCertificate = false;
                    certificateReview.Visibility = Visibility.Collapsed;
                    SetEditorBusy(false);
                    SetStatus("Connection cancelled. The router certificate was not trusted.");
                };
            }
            catch (OperationCanceledException)
            {
                SetStatus("RouterOS connection cancelled.");
            }
            catch (Exception exception)
            {
                SetStatus(exception.Message, isError: true);
            }
            finally
            {
                SetEditorBusy(false);
            }
        }

        loadInterfacesButton.Click += async (_, _) => await LoadInterfacesAsync(allowCertificateReview: true);
        modalRequest = new ModalRequest
        {
            Title = existing is null ? "Add RouterOS Connection" : "Edit RouterOS Connection",
            Subtitle = "Name this router and enter its REST connection details. WireRoute protects the password with current-user Windows DPAPI.",
            Content = content,
            PrimaryText = "Save Connection",
            MaxWidth = 920,
            OnPrimary = async () =>
            {
                try
                {
                    saved = ReadConnectionFields();
                    await routerOSConnectionStore.SaveAsync(saved);
                    return true;
                }
                catch (Exception exception)
                {
                    saved = null;
                    SetStatus(exception.Message, isError: true);
                    return false;
                }
            },
        };

        _ = await ShowModalAsync(modalRequest);
        if (saved is not null)
        {
            await LoadRouterOSConnectionsAsync(saved.Id);
        }
    }

    private sealed record RouterOSInterfaceChoice(string DisplayName, string? InterfaceName)
    {
        public override string ToString() => DisplayName;
    }

    private async void RouterOSRemoveConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (RouterOSConnectionsList.SelectedItem is not RouterOSConnectionRow connectionRow)
        {
            return;
        }

        var connection = connectionRow.Connection;

        var content = new TextBlock
        {
            MaxWidth = 520,
            Text = "This removes the saved connection and its password from this PC. It does not change RouterOS.",
            TextWrapping = TextWrapping.Wrap,
        };
        var result = await ShowModalAsync(new ModalRequest
        {
            Title = $"Remove ‘{connection.Name}’?",
            Content = content,
            PrimaryText = "Remove Connection",
            CancelText = "Cancel",
            MaxWidth = 620,
        });
        if (result != WireRouteModalResult.Primary)
        {
            return;
        }

        try
        {
            await routerOSConnectionStore.DeleteAsync(connection.Id);
            if (routerOSConnectedContext?.Connection.Id == connection.Id)
            {
                InvalidateRouterOSDiscovery("The saved RouterOS connection was removed.");
            }

            await LoadRouterOSConnectionsAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Connection could not be removed", exception.Message);
        }
    }

    private sealed record RouterOSConnectedContext(
        RouterOSStoredConnection Connection,
        RouterOSServerCertificate? TrustedCertificate);
}
