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
            MaxWidth = 900,
            OnPrimary = async () =>
            {
                try
                {
                    var name = nameBox.Text.Trim();
                    var configuration = configurationBox.Text.Trim() + Environment.NewLine;
                    var parsed = WireGuardConfigParser.Parse(configuration, name);
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
                        ProfilesList.SelectedItem = created;
                    }
                    else
                    {
                        item.UpdateStoredProfile(stored, parsed);
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
            Text = string.Join(", ", stored.DnsBootstrapAddresses),
        };
        var profilePanel = new StackPanel { Spacing = 10 };
        profilePanel.Children.Add(SectionLabel("Configured DNS servers"));
        profilePanel.Children.Add(configuredDns);
        profilePanel.Children.Add(SecondaryText(
            "These values are written directly to the WireGuard profile and use the VPN when covered by Allowed IPs."));
        var encryptedPanel = new StackPanel { Spacing = 10 };
        encryptedPanel.Children.Add(SectionLabel("Resolver"));
        encryptedPanel.Children.Add(providerBox);
        encryptedPanel.Children.Add(SectionLabel("Resolver URL"));
        encryptedPanel.Children.Add(resolverBox);
        encryptedPanel.Children.Add(SectionLabel("Bootstrap addresses"));
        encryptedPanel.Children.Add(bootstrapBox);
        encryptedPanel.Children.Add(SecondaryText(
            "Optional resolver IP addresses used when the resolver hostname cannot be reached without DNS."));
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
            }
        };
        UpdateMode();

        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = "DNS Protection",
            Subtitle = "Choose how this profile resolves domain names while connected. Encrypted DNS sends queries to the resolver you select.",
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

    private async void ProfileHistoryButton_Click(object sender, RoutedEventArgs e) =>
        await ShowLogAsync(
            "Activity history",
            "No previous connection activity has been recorded for this profile.");

    private async void ViewLogMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ShowLogAsync(
            "WireRoute Log",
            "WireRoute started in service-free mode. No tunnel log entries are available yet.");

    private async Task ShowLogAsync(string title, string message)
    {
        var logBox = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Mono"),
            IsReadOnly = true,
            MinHeight = 360,
            Text = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + "    " + message,
            TextWrapping = TextWrapping.Wrap,
        };
        await ShowModalAsync(new ModalRequest
        {
            Title = title,
            Content = ModalCard(logBox),
            CancelText = "Close",
            MaxWidth = 960,
        });
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
