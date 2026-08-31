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
            RebuildRouterOSDiscovery();
            RouterOSConnectButton.Content = "Connected";
            ToolTipService.SetToolTip(
                RouterOSConnectButton,
                "Connected to this router. Click to refresh discovery.");
            RouterOSShowAllPeersCheckBox.IsEnabled = routerOSPeers.Count > 0;
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

        var dialog = CreateDialog(
            isReplacement ? "Router Certificate Changed" : "Verify Router Certificate",
            content);
        dialog.PrimaryButtonText = isReplacement ? "Replace Trust and Connect" : "Trust and Connect";
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["NordicAccentButtonStyle"];
        dialog.CloseButtonText = "Cancel";
        dialog.CloseButtonStyle = null;
        dialog.DefaultButton = ContentDialogButton.None;
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
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
        RouterOSSummaryText.Text = "Not connected";
        RouterOSDiscoveryEmptyText.Text = "Connect to securely discover WireGuard peers.";
        RouterOSDiscoveryEmptyText.Visibility = Visibility.Visible;
        RouterOSShowAllPeersCheckBox.IsChecked = false;
        RouterOSShowAllPeersCheckBox.IsEnabled = false;
        RouterOSSetUpPeerButton.IsEnabled = false;
        RouterOSConnectButton.Content = "Connect";
        ToolTipService.SetToolTip(RouterOSConnectButton, null);
        if (status is not null)
        {
            SetRouterOSStatus(status);
        }
    }

    private void RebuildRouterOSDiscovery()
    {
        var managedPeers = routerOSPeers
            .Where(peer => RouterOSPeerCreation.IsWireRouteManagedComment(peer.Comment))
            .ToArray();
        var displayedPeers = RouterOSShowAllPeersCheckBox.IsChecked == true
            ? routerOSPeers
            : managedPeers;
        RouterOSDiscoveryRows.Clear();
        foreach (var routerInterface in routerOSInterfaces)
        {
            RouterOSDiscoveryRows.Add(RouterOSDiscoveryRow.FromInterface(routerInterface));
        }

        foreach (var peer in displayedPeers)
        {
            RouterOSDiscoveryRows.Add(RouterOSDiscoveryRow.FromPeer(peer));
        }

        RouterOSSummaryText.Text = RouterOSShowAllPeersCheckBox.IsChecked == true
            ? $"{routerOSPeers.Count} peers"
            : $"{managedPeers.Length} WireRoute clients • {routerOSPeers.Count} total peers";
        RouterOSDiscoveryEmptyText.Text = routerOSPeers.Count == 0
            ? "No WireGuard peers were found on this router."
            : managedPeers.Length == 0 && RouterOSShowAllPeersCheckBox.IsChecked != true
                ? "No WireRoute-managed clients were found. Select Show all peers to view other peers."
                : string.Empty;
        RouterOSDiscoveryEmptyText.Visibility = RouterOSDiscoveryRows.Count == 0
            || (routerOSPeers.Count > 0 && displayedPeers.Count == 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

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
        var nameField = new TextBox
        {
            Header = "Name",
            PlaceholderText = "Home Router",
            Text = existing?.Name ?? string.Empty,
        };
        var addressField = new TextBox
        {
            Header = "Router address",
            PlaceholderText = "https://router.example",
            Text = existing?.Url ?? string.Empty,
        };
        var usernameField = new TextBox
        {
            Header = "Username",
            PlaceholderText = "RouterOS API user",
            Text = existing?.Username ?? string.Empty,
        };
        var passwordField = new PasswordBox
        {
            Header = "Password",
            PlaceholderText = existing is null
                ? "Required; protected with Windows DPAPI"
                : "Leave blank to keep the saved password",
        };
        var defaultInterfaceField = new TextBox
        {
            Header = "Default interface",
            PlaceholderText = "Automatic",
            Text = existing?.DefaultInterface ?? string.Empty,
        };
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { MinWidth = 520, Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            Text = "Name this router and enter its REST connection details. WireRoute protects the password with current-user Windows DPAPI.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameField);
        content.Children.Add(addressField);
        content.Children.Add(usernameField);
        content.Children.Add(passwordField);
        content.Children.Add(defaultInterfaceField);
        content.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            FontSize = 12,
            Text = "Optional. Enter the WireGuard interface to preselect when setting up a device.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(errorText);

        var dialog = CreateDialog(
            existing is null ? "Add RouterOS Connection" : "Edit RouterOS Connection",
            content);
        dialog.PrimaryButtonText = "Save Connection";
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["NordicAccentButtonStyle"];
        dialog.CloseButtonText = "Cancel";
        dialog.CloseButtonStyle = null;
        dialog.DefaultButton = ContentDialogButton.Primary;
        RouterOSStoredConnection? saved = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
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

                saved = new RouterOSStoredConnection(
                    existing?.Id ?? Guid.NewGuid(),
                    name,
                    url.AbsoluteUri,
                    username,
                    password,
                    string.IsNullOrWhiteSpace(defaultInterfaceField.Text)
                        ? null
                        : defaultInterfaceField.Text.Trim());
                await routerOSConnectionStore.SaveAsync(saved);
            }
            catch (Exception exception)
            {
                args.Cancel = true;
                errorText.Text = exception.Message;
                errorText.Visibility = Visibility.Visible;
            }
            finally
            {
                deferral.Complete();
            }
        };

        _ = await dialog.ShowAsync();
        if (saved is not null)
        {
            await LoadRouterOSConnectionsAsync(saved.Id);
        }
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
        var dialog = CreateDialog($"Remove ‘{connection.Name}’?", content);
        dialog.PrimaryButtonText = "Remove Connection";
        dialog.CloseButtonText = "Cancel";
        dialog.CloseButtonStyle = null;
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
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
