using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WireRoute.App.Models;
using WireRoute.Core.Profiles;
using WireRoute.Core.Routing;
using WireRoute.RouterOS;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private static readonly IReadOnlyDictionary<string, string> EncryptedDnsProviders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cloudflare"] = "https://cloudflare-dns.com/dns-query",
            ["Cloudflare Security"] = "https://security.cloudflare-dns.com/dns-query",
            ["Cloudflare Family"] = "https://family.cloudflare-dns.com/dns-query",
            ["AdGuard DNS"] = "https://dns.adguard-dns.com/dns-query",
            ["AdGuard Family"] = "https://family.adguard-dns.com/dns-query",
            ["Quad9 Secure"] = "https://dns.quad9.net/dns-query",
            ["Google Public DNS"] = "https://dns.google/dns-query",
            ["Custom"] = string.Empty,
        };
    private static readonly IReadOnlyDictionary<string, string[]> EncryptedDnsBootstrap =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Cloudflare"] = ["1.1.1.1", "1.0.0.1"],
            ["Cloudflare Security"] = ["1.1.1.2", "1.0.0.2"],
            ["Cloudflare Family"] = ["1.1.1.3", "1.0.0.3"],
            ["AdGuard DNS"] = ["94.140.14.14", "94.140.15.15"],
            ["AdGuard Family"] = ["94.140.14.15", "94.140.15.16"],
            ["Quad9 Secure"] = ["9.9.9.9", "149.112.112.112"],
            ["Google Public DNS"] = ["8.8.8.8", "8.8.4.4"],
            ["Custom"] = [],
        };

    private async void AddEmptyProfileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var keys = WireGuardKeyPair.Generate();
        var configuration = "[Interface]" + Environment.NewLine
            + "PrivateKey = " + keys.PrivateKey + Environment.NewLine;
        await ShowProfileEditorAsync(null, configuration, keys.PublicKey);
    }

    private async void ImportProfileMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ImportProfilesAsync();

    private async void ProfileEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedProfile?.StoredProfile is null)
        {
            await ShowMessageAsync(
                "This profile cannot be edited here",
                "Import it into WireRoute's protected local profile store first.");
            return;
        }
        if (!EnsureProfileInactive(selectedProfile))
        {
            return;
        }
        await ShowProfileEditorAsync(
            selectedProfile,
            selectedProfile.StoredProfile.Configuration,
            "Calculated by WireGuardNT");
    }

    private async Task ShowProfileEditorAsync(
        ProfileNavigationItem? item,
        string initialConfiguration,
        string publicKey)
    {
        var nameBox = new TextBox
        {
            Text = item?.Name ?? string.Empty,
            PlaceholderText = "Profile name",
        };
        var ethernetBox = new CheckBox
        {
            Content = "Ethernet",
            IsChecked = item?.StoredProfile?.OnDemandEthernet == true,
        };
        var wifiBox = new CheckBox
        {
            Content = "Wi-Fi",
            IsChecked = item?.StoredProfile?.OnDemandWiFi == true,
        };
        var configurationBox = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            MinHeight = 260,
            Text = initialConfiguration,
            TextWrapping = TextWrapping.NoWrap,
        };
        var errorText = ModalErrorText();
        var excludePrivateIpsBox = new CheckBox
        {
            Content = "Exclude private IPs",
            Visibility = Visibility.Collapsed,
        };
        IReadOnlyList<string>? dnsServersAddedToAllowedIps = null;
        var updatingPrivateRouteControl = false;

        string EditorProfileName()
        {
            var proposedName = nameBox.Text.Trim();
            return WireGuardConfigParser.IsValidProfileName(proposedName)
                ? proposedName
                : item?.Name ?? "WireRoute";
        }

        void UpdatePrivateRouteControl()
        {
            if (updatingPrivateRouteControl)
            {
                return;
            }

            try
            {
                var parsed = WireGuardConfigParser.Parse(configurationBox.Text, EditorProfileName());
                var state = WireGuardPrivateRouteExclusion.Evaluate(parsed);
                updatingPrivateRouteControl = true;
                excludePrivateIpsBox.Visibility = state.IsAvailable
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                excludePrivateIpsBox.IsChecked = state.IsEnabled;
            }
            catch (WireGuardConfigParseException)
            {
                updatingPrivateRouteControl = true;
                excludePrivateIpsBox.Visibility = Visibility.Collapsed;
                excludePrivateIpsBox.IsChecked = false;
            }
            finally
            {
                updatingPrivateRouteControl = false;
            }
        }

        try
        {
            var initialProfile = WireGuardConfigParser.Parse(initialConfiguration, EditorProfileName());
            var initialState = WireGuardPrivateRouteExclusion.Evaluate(initialProfile);
            if (initialState.IsEnabled)
            {
                dnsServersAddedToAllowedIps = initialProfile.Interface.DnsServers.ToArray();
            }
        }
        catch (WireGuardConfigParseException)
        {
        }

        configurationBox.TextChanged += (_, _) => UpdatePrivateRouteControl();
        excludePrivateIpsBox.Click += (_, _) =>
        {
            if (updatingPrivateRouteControl)
            {
                return;
            }

            try
            {
                var parsed = WireGuardConfigParser.Parse(configurationBox.Text, EditorProfileName());
                var enable = excludePrivateIpsBox.IsChecked == true;
                updatingPrivateRouteControl = true;
                configurationBox.Text = WireGuardPrivateRouteExclusion.SetEnabled(
                    parsed,
                    enable,
                    dnsServersAddedToAllowedIps);
                dnsServersAddedToAllowedIps = enable
                    ? parsed.Interface.DnsServers.ToArray()
                    : null;
            }
            catch (Exception exception) when (
                exception is WireGuardConfigParseException or ArgumentException)
            {
                excludePrivateIpsBox.IsChecked = excludePrivateIpsBox.IsChecked != true;
                errorText.Text = exception.Message;
                errorText.Visibility = Visibility.Visible;
            }
            finally
            {
                updatingPrivateRouteControl = false;
                UpdatePrivateRouteControl();
            }
        };
        var identityGrid = new Grid { RowSpacing = 10, ColumnSpacing = 12 };
        identityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        identityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 3; index++)
        {
            identityGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        AddFormRow(identityGrid, 0, "Name:", nameBox);
        AddFormRow(identityGrid, 1, "Public key:", new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            Text = publicKey,
            TextWrapping = TextWrapping.Wrap,
        });
        var onDemand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        onDemand.Children.Add(ethernetBox);
        onDemand.Children.Add(wifiBox);
        AddFormRow(identityGrid, 2, "On-Demand:", onDemand);

        var editorStack = new StackPanel { Spacing = 10 };
        editorStack.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Configuration details",
        });
        editorStack.Children.Add(configurationBox);
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(ModalCard(identityGrid));
        content.Children.Add(ModalCard(editorStack));
        content.Children.Add(errorText);

        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = item is null ? "New configuration" : "Edit configuration",
            IconGlyph = "\uE713",
            Content = content,
            PrimaryText = "Save",
            CancelText = item is null ? "Discard" : "Cancel",
            LeadingFooterContent = excludePrivateIpsBox,
            MaxWidth = 900,
            OnPrimary = async () =>
            {
                try
                {
                    var name = nameBox.Text.Trim();
                    var configuration = configurationBox.Text.Trim() + Environment.NewLine;
                    var parsed = WireGuardConfigParser.Parse(configuration, name);
                    if (excludePrivateIpsBox.IsChecked == true
                        && dnsServersAddedToAllowedIps is not null
                        && parsed.Peers.Count == 1)
                    {
                        configuration = WireGuardPrivateRouteExclusion.RefreshDnsRoutes(
                            parsed,
                            dnsServersAddedToAllowedIps);
                        parsed = WireGuardConfigParser.Parse(configuration, name);
                    }
                    var existing = item?.StoredProfile;
                    var now = DateTimeOffset.UtcNow;
                    var stored = new WireRouteStoredProfile(
                        existing?.Id ?? Guid.NewGuid(),
                        name,
                        configuration,
                        parsed.DetectedRouteMode == TunnelRouteMode.Full
                            ? StoredTunnelRouteMode.Full
                            : StoredTunnelRouteMode.Split,
                        parsed.SuggestedSplitAllowedIps.Select(route => route.Notation).ToArray(),
                        existing?.DnsProtectionMode ?? StoredDnsProtectionMode.Profile,
                        existing?.DnsProvider,
                        existing?.DnsResolverUrl,
                        existing?.DnsBootstrapAddresses ?? Array.Empty<string>(),
                        ethernetBox.IsChecked == true,
                        wifiBox.IsChecked == true,
                        existing?.CreatedAt ?? now,
                        now);
                    await profileStore.SaveAsync(stored, managerCancellation.Token);
                    if (item is null)
                    {
                        var created = new ProfileNavigationItem(stored, parsed);
                        Profiles.Add(created);
                        await RecordActivityAsync(
                            WireRouteActivityKind.ProfileCreated,
                            created,
                            "Created a protected WireGuard profile.");
                        ProfilesList.SelectedItem = created;
                    }
                    else
                    {
                        item.UpdateStoredProfile(stored, parsed);
                        await RecordActivityAsync(
                            WireRouteActivityKind.ProfileUpdated,
                            item,
                            "Updated the WireGuard configuration.");
                        RefreshProfileListItem(item);
                        await ShowProfileAsync(item);
                    }
                    return true;
                }
                catch (Exception exception) when (
                    exception is WireGuardConfigParseException
                    or ArgumentException
                    or WireRouteStorageException)
                {
                    return KeepModalOpen(request!, errorText, exception.Message);
                }
            },
        };
        var modal = ShowModalAsync(request);
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdatePrivateRouteControl();
            nameBox.Focus(FocusState.Programmatic);
            nameBox.SelectAll();
        });
        await modal;
    }

    private async void ProfileSplitButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedProfile is not null)
        {
            await ShowSplitRoutesAsync(selectedProfile);
        }
    }

    private async void ProfileFullButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedProfile is null || !EnsureEditableLocalProfile(selectedProfile))
        {
            return;
        }
        if (selectedProfile.Profile!.Peers.Count != 1)
        {
            await ShowMessageAsync(
                "Traffic routing cannot be changed",
                "Profiles with more than one peer must be edited in the configuration editor.");
            return;
        }
        var routes = new List<RoutePrefix> { new("0.0.0.0/0") };
        if (selectedProfile.Profile.Interface.Addresses.Any(route => route.Family == IpFamily.Ipv6)
            || selectedProfile.Profile.ImportedAllowedIps.Any(route => route.Family == IpFamily.Ipv6))
        {
            routes.Add(new RoutePrefix("::/0"));
        }
        await SaveRoutingAsync(selectedProfile, StoredTunnelRouteMode.Full, routes);
    }

    private async Task ShowSplitRoutesAsync(ProfileNavigationItem item)
    {
        if (!EnsureEditableLocalProfile(item))
        {
            return;
        }
        if (item.Profile!.Peers.Count != 1)
        {
            await ShowMessageAsync(
                "Traffic routing cannot be changed",
                "Profiles with more than one peer must be edited in the configuration editor.");
            return;
        }
        var values = item.StoredProfile!.SplitRoutes.Count > 0
            ? item.StoredProfile.SplitRoutes
            : item.Profile.SuggestedSplitAllowedIps.Select(route => route.Notation).ToArray();
        var routesBox = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono"),
            MinHeight = 220,
            Text = string.Join(Environment.NewLine, values),
            TextWrapping = TextWrapping.NoWrap,
        };
        var help = new StackPanel { Spacing = 5 };
        help.Children.Add(new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Not sure what to enter?",
        });
        help.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            Text = "Add networks behind this VPN, such as your home, office, or VPN address range.\nExamples only — replace them with your networks:\n192.168.50.0/24\n10.20.0.0/16",
            TextWrapping = TextWrapping.Wrap,
        });
        var errorText = ModalErrorText();
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(ModalCard(help, raised: true));
        body.Children.Add(routesBox);
        body.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            FontSize = 12,
            Text = "Use CIDR notation, one network per line. Default routes belong to Full tunnel.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(errorText);
        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = "Choose split routes",
            Subtitle = "Enter the networks that should use this VPN. Other traffic stays on this PC's normal connection.",
            Content = ModalCard(body),
            PrimaryText = "Save",
            MaxWidth = 780,
            OnPrimary = async () =>
            {
                try
                {
                    var routes = routesBox.Text
                        .Split(["\r\n", "\n", "\r", ","], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(value => new RoutePrefix(value))
                        .Distinct()
                        .ToArray();
                    if (routes.Length == 0)
                    {
                        throw new ArgumentException("Enter at least one split route.");
                    }
                    if (routes.Any(route => route.IsDefaultRoute))
                    {
                        throw new ArgumentException("Use Full routing for 0.0.0.0/0 or ::/0.");
                    }
                    await SaveRoutingAsync(item, StoredTunnelRouteMode.Split, routes);
                    return true;
                }
                catch (Exception exception) when (
                    exception is RoutePrefixValidationException
                    or ArgumentException
                    or WireRouteStorageException)
                {
                    return KeepModalOpen(request!, errorText, exception.Message);
                }
            },
        };
        await ShowModalAsync(request);
    }

    private async Task SaveRoutingAsync(
        ProfileNavigationItem item,
        StoredTunnelRouteMode mode,
        IReadOnlyList<RoutePrefix> routes)
    {
        var configuration = WireGuardConfigFormatter.ToWgQuick(item.Profile!, routes);
        var parsed = WireGuardConfigParser.Parse(configuration, item.Name);
        var stored = item.StoredProfile! with
        {
            Configuration = configuration,
            RouteMode = mode,
            SplitRoutes = mode == StoredTunnelRouteMode.Split
                ? routes.Select(route => route.Notation).ToArray()
                : item.StoredProfile.SplitRoutes,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await profileStore.SaveAsync(stored, managerCancellation.Token);
        item.UpdateStoredProfile(stored, parsed);
        await RecordActivityAsync(
            WireRouteActivityKind.ProfileUpdated,
            item,
            mode == StoredTunnelRouteMode.Full
                ? "Changed traffic routing to Full."
                : "Changed traffic routing to Split.");
        RefreshProfileListItem(item);
        await ShowProfileAsync(item);
    }

    private async void ProfileDnsProtectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedProfile is not null)
        {
            await ShowDnsProtectionAsync(selectedProfile);
        }
    }

    private async Task ShowDnsProtectionAsync(ProfileNavigationItem item)
    {
        if (!EnsureEditableLocalProfile(item))
        {
            return;
        }
        var stored = item.StoredProfile!;
        var profileMode = new Button
        {
            Content = "Profile DNS",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var encryptedMode = new Button
        {
            Content = "Encrypted DNS",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var modeGrid = new Grid
        {
            Height = 40,
            Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
            CornerRadius = new CornerRadius(6),
        };
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(encryptedMode, 1);
        modeGrid.Children.Add(profileMode);
        modeGrid.Children.Add(encryptedMode);

        var configuredDns = new TextBox
        {
            Text = string.Join(", ", item.Profile!.Interface.DnsServers),
            PlaceholderText = "DNS addresses from this WireGuard profile",
            Visibility = Visibility.Collapsed,
        };
        var providerBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = EncryptedDnsProviders.Keys.ToArray(),
        };
        providerBox.SelectedItem = stored.DnsProvider is not null
            && EncryptedDnsProviders.ContainsKey(stored.DnsProvider)
                ? stored.DnsProvider
                : "Cloudflare";
        var resolverBox = new TextBox
        {
            PlaceholderText = "https://resolver.example/dns-query",
            Text = stored.DnsResolverUrl
                ?? EncryptedDnsProviders[(string)providerBox.SelectedItem],
        };
        var bootstrapBox = new TextBox
        {
            PlaceholderText = "Optional IPv4 or IPv6 addresses",
            Text = stored.DnsBootstrapAddresses.Count > 0
                ? string.Join(", ", stored.DnsBootstrapAddresses)
                : string.Join(
                    ", ",
                    EncryptedDnsBootstrap[(string)providerBox.SelectedItem]),
        };
        var profilePanel = new StackPanel { Spacing = 10 };
        profilePanel.Children.Add(SectionLabel("Profile resolution path"));
        profilePanel.Children.Add(SecondaryText(
            "The DNS values below come directly from this WireGuard configuration."));
        profilePanel.Children.Add(SectionLabel("Configured DNS servers"));
        var dnsRows = new StackPanel { Spacing = 8 };
        foreach (var server in item.Profile.DnsRouteSummary.Servers)
        {
            var row = new Grid
            {
                Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                ColumnSpacing = 10,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["NordicAccentBrush"],
                Glyph = "\uE968",
            });
            var address = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = server.Address,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(address, 1);
            row.Children.Add(address);
            var routeChip = new Border
            {
                Background = server.Route == DnsServerRoute.ThroughTunnel
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(46, 53, 205, 98))
                    : (Brush)Application.Current.Resources["NordicInsetBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock
                {
                    Foreground = server.Route == DnsServerRoute.ThroughTunnel
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 225, 116))
                        : (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Text = server.Route == DnsServerRoute.ThroughTunnel ? "Via VPN" : "Outside VPN",
                },
            };
            Grid.SetColumn(routeChip, 2);
            row.Children.Add(routeChip);
            dnsRows.Children.Add(row);
        }
        if (dnsRows.Children.Count == 0)
        {
            dnsRows.Children.Add(SecondaryText("No DNS servers are configured in this profile."));
        }
        profilePanel.Children.Add(dnsRows);
        profilePanel.Children.Add(configuredDns);
        var editDnsButton = new Button
        {
            Content = "Edit Profile DNS…",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var editingProfileDns = false;
        editDnsButton.Click += (_, _) =>
        {
            editingProfileDns = !editingProfileDns;
            configuredDns.Visibility = editingProfileDns ? Visibility.Visible : Visibility.Collapsed;
            dnsRows.Visibility = editingProfileDns ? Visibility.Collapsed : Visibility.Visible;
            editDnsButton.Content = editingProfileDns ? "Done Editing" : "Edit Profile DNS…";
            if (editingProfileDns)
            {
                configuredDns.Focus(FocusState.Programmatic);
            }
        };
        profilePanel.Children.Add(editDnsButton);
        var encryptedPanel = new StackPanel { Spacing = 10 };
        encryptedPanel.Children.Add(SectionLabel("Resolver"));
        encryptedPanel.Children.Add(providerBox);
        encryptedPanel.Children.Add(SectionLabel("Resolver URL"));
        encryptedPanel.Children.Add(resolverBox);
        encryptedPanel.Children.Add(SectionLabel("Bootstrap addresses"));
        encryptedPanel.Children.Add(bootstrapBox);
        encryptedPanel.Children.Add(SecondaryText(
            "WireRoute listens only on 127.0.0.1 while this tunnel is active and sends DNS queries "
            + "to this HTTPS resolver. Bootstrap addresses avoid a plaintext hostname lookup."));
        var errorText = ModalErrorText();
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(SectionLabel("Protection mode"));
        body.Children.Add(modeGrid);
        body.Children.Add(profilePanel);
        body.Children.Add(encryptedPanel);
        body.Children.Add(errorText);

        var encrypted = stored.DnsProtectionMode == StoredDnsProtectionMode.Encrypted;
        void UpdateMode()
        {
            profilePanel.Visibility = encrypted ? Visibility.Collapsed : Visibility.Visible;
            encryptedPanel.Visibility = encrypted ? Visibility.Visible : Visibility.Collapsed;
            profileMode.Background = encrypted
                ? (Brush)Application.Current.Resources["NordicRaisedBrush"]
                : (Brush)Application.Current.Resources["NordicAccentBrush"];
            encryptedMode.Background = encrypted
                ? (Brush)Application.Current.Resources["NordicAccentBrush"]
                : (Brush)Application.Current.Resources["NordicRaisedBrush"];
        }
        profileMode.Click += (_, _) => { encrypted = false; UpdateMode(); };
        encryptedMode.Click += (_, _) => { encrypted = true; UpdateMode(); };
        providerBox.SelectionChanged += (_, _) =>
        {
            if (providerBox.SelectedItem is string provider
                && !provider.Equals("Custom", StringComparison.Ordinal))
            {
                resolverBox.Text = EncryptedDnsProviders[provider];
                bootstrapBox.Text = string.Join(", ", EncryptedDnsBootstrap[provider]);
            }
        };
        UpdateMode();

        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = "DNS Protection",
            Subtitle = "Choose how this profile resolves domain names while connected. "
                + "Encrypted DNS uses WireRoute's service-free loopback proxy and does not change system-wide DNS settings.",
            IconGlyph = "\uEA18",
            Content = ModalCard(body),
            PrimaryText = "Save",
            MaxWidth = 900,
            OnPrimary = async () =>
            {
                try
                {
                    var current = item.StoredProfile!;
                    var configuration = current.Configuration;
                    if (!encrypted)
                    {
                        var dns = configuredDns.Text
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        configuration = WireGuardConfigFormatter.ToWgQuick(
                            item.Profile!,
                            dnsServers: dns);
                        if (WireGuardPrivateRouteExclusion.Evaluate(item.Profile).IsEnabled)
                        {
                            var parsedDnsChange = WireGuardConfigParser.Parse(configuration, item.Name);
                            configuration = WireGuardPrivateRouteExclusion.RefreshDnsRoutes(
                                parsedDnsChange,
                                item.Profile.Interface.DnsServers);
                        }
                    }
                    var provider = providerBox.SelectedItem as string ?? "Custom";
                    var resolver = encrypted ? resolverBox.Text.Trim() : null;
                    if (encrypted
                        && (!Uri.TryCreate(resolver, UriKind.Absolute, out var resolverUri)
                            || resolverUri.Scheme != Uri.UriSchemeHttps))
                    {
                        throw new ArgumentException("Enter a complete HTTPS resolver URL.");
                    }
                    var bootstrap = bootstrapBox.Text
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToArray();
                    if (bootstrap.Any(value => !IPAddress.TryParse(value, out _)))
                    {
                        throw new ArgumentException(
                            "Bootstrap addresses must be valid IPv4 or IPv6 addresses.");
                    }
                    var updated = current with
                    {
                        Configuration = configuration,
                        DnsProtectionMode = encrypted
                            ? StoredDnsProtectionMode.Encrypted
                            : StoredDnsProtectionMode.Profile,
                        DnsProvider = encrypted ? provider : null,
                        DnsResolverUrl = resolver,
                        DnsBootstrapAddresses = bootstrap,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    var parsed = WireGuardConfigParser.Parse(configuration, item.Name);
                    await profileStore.SaveAsync(updated, managerCancellation.Token);
                    item.UpdateStoredProfile(updated, parsed);
                    await RecordActivityAsync(
                        WireRouteActivityKind.ProfileUpdated,
                        item,
                        encrypted
                            ? "Changed DNS protection to " + provider + "."
                            : "Changed DNS protection to Profile DNS.");
                    RefreshProfileListItem(item);
                    await ShowProfileAsync(item);
                    return true;
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    or WireGuardConfigParseException
                    or WireRouteStorageException)
                {
                    return KeepModalOpen(request!, errorText, exception.Message);
                }
            },
        };
        await ShowModalAsync(request);
    }

    private async void ProfileHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var item = selectedProfile;
        if (item is null)
        {
            await ShowMessageAsync("No profile selected", "Select a profile to view its activity history.");
            return;
        }

        var entries = await LoadProfileActivityAsync(item);
        await ShowActivityLogAsync(item.Name + " Activity", entries);
    }

    private async void ViewLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var nativeLogs = Profiles
            .Where(profile => profile.StoredProfile is not null)
            .Select(profile => (
                ProfileName: profile.Name,
                Text: localTunnelController.ReadRuntimeLog(profile.StoredProfile!)))
            .Where(log => !string.IsNullOrWhiteSpace(log.Text))
            .ToArray();
        await ShowActivityLogAsync(
            "WireRoute Log",
            await activityStore.LoadAsync(),
            nativeLogs);
    }

    private async Task ShowActivityLogAsync(
        string title,
        IReadOnlyList<WireRouteActivityEntry> entries,
        IReadOnlyList<(string ProfileName, string Text)>? nativeLogs = null)
    {
        var sections = new List<string>();
        if (entries.Count > 0)
        {
            sections.Add(string.Join(
                Environment.NewLine,
                entries.Select(entry =>
                    entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + "    [WireRoute] " + entry.Kind
                    + "    " + (entry.ProfileName ?? "WireRoute")
                    + "    " + entry.Message)));
        }
        if (nativeLogs is not null)
        {
            sections.AddRange(nativeLogs.Select(log =>
                "WireGuardNT tunnel: " + log.ProfileName
                + Environment.NewLine
                + log.Text));
        }
        var text = sections.Count == 0
            ? "No WireRoute or WireGuardNT activity has been recorded yet."
            : string.Join(Environment.NewLine + Environment.NewLine, sections);
        var logBox = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono"),
            IsReadOnly = true,
            MinHeight = 360,
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };

        var header = new Grid { ColumnSpacing = 18 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Time",
        });
        var messageHeader = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Log message",
        };
        Grid.SetColumn(messageHeader, 1);
        header.Children.Add(messageHeader);

        var logTable = new StackPanel { Spacing = 8 };
        logTable.Children.Add(header);
        logTable.Children.Add(logBox);

        var errorText = ModalErrorText();
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(ModalCard(logTable));
        content.Children.Add(errorText);
        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = title,
            Subtitle = nativeLogs is { Count: > 0 }
                ? "Protected WireRoute lifecycle history and native WireGuardNT tunnel events."
                : "Protected app and tunnel lifecycle events recorded by WireRoute.",
            Content = content,
            PrimaryText = "Save…",
            CancelText = "Close",
            MaxWidth = 960,
            OnPrimary = async () =>
            {
                try
                {
                    var picker = new FileSavePicker
                    {
                        SuggestedFileName = "WireRoute-activity-"
                            + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"),
                        SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    };
                    picker.FileTypeChoices.Add("Log file", new[] { ".log" });
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
                    var file = await picker.PickSaveFileAsync();
                    if (file is null)
                    {
                        return false;
                    }
                    await Windows.Storage.FileIO.WriteTextAsync(file, text);
                    return true;
                }
                catch (Exception exception)
                {
                    return KeepModalOpen(request!, errorText, exception.Message);
                }
            },
        };
        await ShowModalAsync(request);
    }

    private async Task<IReadOnlyList<WireRouteActivityEntry>> LoadProfileActivityAsync(
        ProfileNavigationItem item)
    {
        if (item.StoredProfile is not null)
        {
            return await activityStore.LoadAsync(
                item.StoredProfile.Id,
                managerCancellation.Token);
        }

        return (await activityStore.LoadAsync(cancellationToken: managerCancellation.Token))
            .Where(entry => entry.ProfileName?.Equals(
                item.Name,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    private async Task UpdateActivitySummaryAsync(ProfileNavigationItem item)
    {
        ProfileDownloadText.Text = "Zero KB/s";
        ProfileUploadText.Text = "Zero KB/s";
        ProfileSessionTotalText.Text = "—";
        ProfileHandshakeText.Text = "—";
        try
        {
            var mostRecent = (await LoadProfileActivityAsync(item)).FirstOrDefault();
            ProfileActivityText.Text = item.IsActive
                ? "Tunnel active. WireRoute is recording connection activity."
                : mostRecent is null
                    ? "Connect this profile to begin recording activity."
                    : "Last activity "
                        + mostRecent.Timestamp.ToLocalTime().ToString("g")
                        + ": " + mostRecent.Message;
        }
        catch (WireRouteStorageException)
        {
            ProfileActivityText.Text = "Activity history is temporarily unavailable.";
        }
    }

    private async Task RecordActivityAsync(
        WireRouteActivityKind kind,
        ProfileNavigationItem? item,
        string message)
    {
        try
        {
            await activityStore.AppendAsync(
                new WireRouteActivityEntry(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    kind,
                    item?.StoredProfile?.Id,
                    item?.Name,
                    message));
        }
        catch (WireRouteStorageException)
        {
            // Tunnel actions must not fail merely because optional history could not be written.
        }
    }

    private async void ExportAllProfilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var profiles = Profiles
            .Where(profile => profile.StoredProfile is not null)
            .Select(profile => profile.StoredProfile!)
            .ToArray();
        if (profiles.Length == 0)
        {
            await ShowMessageAsync(
                "Nothing to export",
                "Add or import a WireGuard profile first.");
            return;
        }
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = "WireRoute-Tunnels",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("Zip archive", new[] { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Create,
                leaveOpen: true);
            foreach (var profile in profiles)
            {
                var entry = archive.CreateEntry(
                    profile.Name + ".conf",
                    CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(
                    entryStream,
                    new UTF8Encoding(false));
                await writer.WriteAsync(profile.Configuration);
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Profiles could not be exported", exception.Message);
        }
    }

    private async void DeleteSelectedProfileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var item = selectedProfile;
        if (item?.StoredProfile is null)
        {
            await ShowMessageAsync(
                "No profile selected",
                "Select a protected local profile to delete.");
            return;
        }
        if (!EnsureProfileInactive(item))
        {
            return;
        }

        await ShowModalAsync(new ModalRequest
        {
            Title = "Delete “" + item.Name + "”?",
            Subtitle = "This removes the protected local profile and its private key from this PC.",
            Content = new TextBlock
            {
                Text = "Export the profile first if you may need it again.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryText = "Delete",
            CancelText = "Cancel",
            MaxWidth = 560,
            OnPrimary = async () =>
            {
                await profileStore.DeleteAsync(
                    item.StoredProfile.Id,
                    managerCancellation.Token);
                await RecordActivityAsync(
                    WireRouteActivityKind.ProfileDeleted,
                    item,
                    "Deleted the protected local profile.");
                Profiles.Remove(item);
                selectedProfile = null;
                ShowDestination(Destination.Profile);
                return true;
            },
        });
    }

    private bool EnsureEditableLocalProfile(ProfileNavigationItem item)
    {
        if (item.StoredProfile is null || item.Profile is null)
        {
            _ = ShowMessageAsync(
                "This profile cannot be changed here",
                "Only profiles stored securely by WireRoute can be edited.");
            return false;
        }
        return EnsureProfileInactive(item);
    }

    private bool EnsureProfileInactive(ProfileNavigationItem item)
    {
        if (!item.IsActive && !item.IsTransitioning)
        {
            return true;
        }
        _ = ShowMessageAsync(
            "Deactivate this profile first",
            "WireRoute does not change or delete a configuration while its tunnel is active.");
        return false;
    }

    private static Border ModalCard(FrameworkElement child, bool raised = false) => new()
    {
        Background = (Brush)Application.Current.Resources[
            raised ? "NordicRaisedBrush" : "NordicSurfaceBrush"],
        BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(20),
        Child = child,
    };

    private static TextBlock ModalErrorText() => new()
    {
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Text = text,
    };

    private static TextBlock SecondaryText(string text) => new()
    {
        Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
        FontSize = 12,
        Text = text,
        TextWrapping = TextWrapping.Wrap,
    };

    private static bool KeepModalOpen(
        ModalRequest request,
        TextBlock errorText,
        string message)
    {
        errorText.Text = message;
        errorText.Visibility = Visibility.Visible;
        request.SetBusy(false);
        return false;
    }

    private static void AddFormRow(
        Grid grid,
        int row,
        string label,
        FrameworkElement field)
    {
        var labelText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = label,
        };
        Grid.SetRow(labelText, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(labelText);
        grid.Children.Add(field);
    }

    private void ProfilesList_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement element
                && element.DataContext is ProfileNavigationItem item)
            {
                ProfilesList.SelectedItem = item;
                if (ProfilesList.ContextFlyout is MenuFlyout flyout
                    && flyout.Items.FirstOrDefault() is MenuFlyoutItem activate)
                {
                    activate.Text = item.IsActive ? "Deactivate" : "Activate";
                }
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private async void ProfileContextActivate_Click(object sender, RoutedEventArgs e)
    {
        if (selectedProfile is not null)
        {
            await ToggleProfileAsync(selectedProfile);
        }
    }

    private async void ProfileContextQr_Click(object sender, RoutedEventArgs e)
    {
        if (selectedProfile?.StoredProfile is null)
        {
            return;
        }
        var configuration = selectedProfile.StoredProfile.Configuration;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(
            configuration,
            QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(8);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        bitmap.SetSource(stream);
        var image = new Image
        {
            Width = 360,
            Height = 360,
            Source = bitmap,
            Stretch = Stretch.Uniform,
        };
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
        };
        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = image,
        });
        content.Children.Add(SecondaryText(
            "This QR code contains the client private key. Show it only to the device that will import this tunnel."));
        var result = await ShowModalAsync(new ModalRequest
        {
            Title = selectedProfile.Name,
            Subtitle = "Scan this WireGuard configuration QR code from the destination device.",
            Content = content,
            SecondaryText = "Copy Configuration",
            CancelText = "Done",
            MaxWidth = 620,
        });
        if (result == WireRouteModalResult.Secondary)
        {
            await CopySensitiveTextAsync(
                configuration,
                "Configuration copied",
                "The sensitive WireGuard configuration is on the clipboard.");
        }
    }

    private async void ProfileContextCopyPublicKey_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (selectedProfile?.Profile is null)
        {
            return;
        }
        var keyPair = WireGuardKeyPair.FromPrivateKey(
            WireGuardConfigFormatter.PrivateKey(selectedProfile.Profile));
        await CopySensitiveTextAsync(
            keyPair.PublicKey,
            "Public key copied",
            "The client public key is on the clipboard.");
    }

    private async void ProfileContextCopyPrivateKey_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (selectedProfile?.Profile is null)
        {
            return;
        }
        await CopySensitiveTextAsync(
            WireGuardConfigFormatter.PrivateKey(selectedProfile.Profile),
            "Private key copied",
            "The client private key is on the clipboard. Clear it after use.");
    }

    private async Task CopySensitiveTextAsync(
        string value,
        string title,
        string message)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(value);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            await ShowMessageAsync(title, message);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Clipboard is unavailable", exception.Message);
        }
    }

    private async void ProfileContextExportSelected_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (selectedProfile?.StoredProfile is not { } profile)
        {
            return;
        }
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = profile.Name,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("Zip archive", new[] { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }
            await using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Create,
                leaveOpen: true);
            var entry = archive.CreateEntry(
                profile.Name + ".conf",
                CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(
                entryStream,
                new UTF8Encoding(false));
            await writer.WriteAsync(profile.Configuration);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Profile could not be exported", exception.Message);
        }
    }

    private void ProfileContextRouterOS_Click(object sender, RoutedEventArgs e) =>
        ShowDestination(Destination.RouterOS);

    private void ProfileContextSettings_Click(object sender, RoutedEventArgs e) =>
        ShowDestination(Destination.Settings);
}
