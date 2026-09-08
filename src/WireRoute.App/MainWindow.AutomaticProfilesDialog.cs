using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WireRoute.App.Interop;
using WireRoute.Core.Profiles;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private async void AutomaticProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeModal is not null) return;
        await ShowAutomaticProfilesAsync();
    }

    private async Task ShowAutomaticProfilesAsync(ModalRequest? parent = null)
    {
        var saved = appSettings.AutomaticProfiles;
        var defaultId = saved.DefaultProfileId;
        var wifi = saved.WiFi;
        var cellular = saved.Cellular;
        var ethernet = saved.Ethernet;
        var assignments = saved.Assignments.ToList();
        var profiles = Profiles.Where(p => p.StoredProfile is not null && !p.IsManaged)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => (Id: p.StoredProfile!.Id, p.Name)).ToArray();
        string ProfileName(Guid? id) => id is null ? "VPN off"
            : profiles.FirstOrDefault(p => p.Id == id).Name ?? "Unavailable profile";
        string TargetName(ProfileTarget target) => target.Kind == ProfileTargetKind.Default ? "Use default profile"
            : target.Kind == ProfileTargetKind.Off ? "VPN off" : ProfileName(target.ProfileId);

        var enable = new CheckBox
        {
            Content = "Enable automatic profiles", IsChecked = saved.Enabled,
            Foreground = (Brush)Application.Current.Resources["NordicPrimaryTextBrush"],
        };
        var resumeLegacy = new CheckBox
        {
            Content = "Resume saved single-profile On-Demand rules when automatic profiles are off",
            IsChecked = false,
            Foreground = (Brush)Application.Current.Resources["NordicPrimaryTextBrush"],
            Visibility = appSettings.SingleProfileOnDemandSuspended || saved.Enabled ? Visibility.Visible : Visibility.Collapsed,
        };
        resumeLegacy.IsEnabled = !saved.Enabled;
        enable.Click += (_, _) =>
        {
            resumeLegacy.IsEnabled = enable.IsChecked != true;
            if (enable.IsChecked == true) resumeLegacy.IsChecked = false;
        };
        var trusted = new TextBox
        {
            Text = string.Join(Environment.NewLine, saved.TrustedSsids),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90,
            PlaceholderText = "Trusted Wi-Fi names (one per line)",
            Background = (Brush)Application.Current.Resources["NordicInsetBrush"],
            Foreground = (Brush)Application.Current.Resources["NordicPrimaryTextBrush"],
            BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(trusted, "Trusted Wi-Fi names, one per line");
        var error = ModalErrorText();
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var form = new StackPanel { Spacing = 16 };
        ModalRequest? request = null;
        Action? returnToForm = null;

        Button RowButton(string text)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 12, 16, 12), MinHeight = 48,
                Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
                Foreground = (Brush)Application.Current.Resources["NordicPrimaryTextBrush"],
                BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
                CornerRadius = new CornerRadius(8),
            };
            return button;
        }
        void SetLabel(Button button, string text) => ((TextBlock)button.Content).Text = text;
        StackPanel Section(string title, string description)
        {
            var section = new StackPanel { Spacing = 10 };
            section.Children.Add(SectionLabel(title));
            section.Children.Add(SecondaryText(description));
            form.Children.Add(ModalCard(section));
            return section;
        }
        void ShowForm()
        {
            host.Content = form;
            returnToForm = null;
            request!.SetPrimaryEnabled(true);
        }
        void ShowSubpage(FrameworkElement page, Button source)
        {
            host.Content = page;
            ModalContentScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            request!.SetPrimaryEnabled(false);
            returnToForm = () => { ShowForm(); source.Focus(FocusState.Programmatic); };
        }
        void PickTarget(string title, ProfileTarget current, bool allowDefault, Button source, Action<ProfileTarget> choose)
        {
            var page = new StackPanel { Spacing = 14 };
            page.Children.Add(SectionLabel(title));
            page.Children.Add(SecondaryText("Choose an action. Cancel returns without changing it."));
            var choices = new List<(string Label, ProfileTarget Value)>();
            if (allowDefault) choices.Add(("Use default profile", ProfileTarget.Default));
            choices.Add(("VPN off", ProfileTarget.Off));
            choices.AddRange(profiles.Select(p => (p.Name, ProfileTarget.ForProfile(p.Id))));
            if (current.Kind == ProfileTargetKind.Profile && !profiles.Any(p => p.Id == current.ProfileId))
                page.Children.Add(SecondaryText("The previously assigned profile is unavailable. Choose another profile or VPN off."));
            var group = Guid.NewGuid().ToString("N");
            foreach (var choice in choices)
            {
                var radio = new RadioButton
                {
                    Content = choice.Label, IsChecked = current == choice.Value,
                    GroupName = group, MinHeight = 42,
                    Foreground = (Brush)Application.Current.Resources["NordicPrimaryTextBrush"],
                };
                radio.Click += (_, _) =>
                {
                    choose(choice.Value);
                    ShowForm();
                    source.Focus(FocusState.Programmatic);
                };
                page.Children.Add(radio);
            }
            ShowSubpage(ModalCard(page), source);
        }

        form.Children.Add(SecondaryText("Choose which saved profile connects as your network changes. Enabling this replaces single-profile On-Demand; its saved rules are kept but paused."));
        form.Children.Add(enable);
        var defaults = Section("Default profile", "Used when a network action says Use default profile. VPN off means no automatic connection.");
        var defaultButton = RowButton("Default: " + ProfileName(defaultId));
        defaultButton.Click += (_, _) => PickTarget("Default profile", defaultId is Guid id ? ProfileTarget.ForProfile(id) : ProfileTarget.Off,
            false, defaultButton, target => { defaultId = target.ProfileId; SetLabel(defaultButton, "Default: " + ProfileName(defaultId)); });
        defaults.Children.Add(defaultButton);
        var actions = Section("Network actions", "Wi-Fi assignments take priority over Other Wi-Fi. Trusted Wi-Fi always selects VPN off for automatically connected profiles.");
        actions.Children.Add(SecondaryText("When several physical networks are connected, Ethernet takes priority over Wi-Fi, then cellular. VPN and other virtual adapters are never treated as Ethernet."));
        var wifiButton = RowButton("Other Wi-Fi: " + TargetName(wifi));
        var cellularButton = RowButton("Cellular: " + TargetName(cellular));
        var ethernetButton = RowButton("Ethernet: " + TargetName(ethernet));
        wifiButton.Click += (_, _) => PickTarget("Other Wi-Fi", wifi, true, wifiButton,
            t => { wifi = t; SetLabel(wifiButton, "Other Wi-Fi: " + TargetName(t)); });
        cellularButton.Click += (_, _) => PickTarget("Cellular", cellular, true, cellularButton,
            t => { cellular = t; SetLabel(cellularButton, "Cellular: " + TargetName(t)); });
        ethernetButton.Click += (_, _) => PickTarget("Ethernet", ethernet, true, ethernetButton,
            t => { ethernet = t; SetLabel(ethernetButton, "Ethernet: " + TargetName(t)); });
        actions.Children.Add(wifiButton);
        actions.Children.Add(cellularButton);
        actions.Children.Add(ethernetButton);
        var trustedSection = Section("Trusted Wi-Fi", "Keep automatically connected VPNs off on these exact network names. One name per line; spelling, capitalization, and spaces matter. A Wi-Fi name is not proof that a network is safe.");
        trustedSection.Children.Add(trusted);
        var currentButton = RowButton("Add current Wi-Fi as trusted");
        currentButton.Click += (_, _) =>
        {
            try
            {
                var current = ProfileNetworkMonitor.Read(new HashSet<string>(), readWiFiNames: true);
                if (current.Transport != ProfileNetworkTransport.WiFi || string.IsNullOrEmpty(current.Ssid))
                    throw new InvalidOperationException("No readable active Wi-Fi name. Connect through Wi-Fi or review Wi-Fi name access below.");
                var names = TrustedLines(trusted.Text).ToList();
                if (!names.Contains(current.Ssid, StringComparer.Ordinal)) names.Add(current.Ssid);
                trusted.Text = string.Join(Environment.NewLine, names);
                error.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception) { error.Text = exception.Message; error.Visibility = Visibility.Visible; }
        };
        trustedSection.Children.Add(currentButton);
        var assignedSection = Section("Wi-Fi profile assignments", "Connect a specific profile on each named Wi-Fi network. All other Wi-Fi uses the action above.");
        var rows = new StackPanel { Spacing = 8 };
        assignedSection.Children.Add(rows);
        var add = RowButton("Add Wi-Fi assignment");
        void RenderAssignments()
        {
            rows.Children.Clear();
            if (assignments.Count == 0) rows.Children.Add(SecondaryText("No network-specific profiles yet."));
            foreach (var assignment in assignments.ToArray())
            {
                var row = RowButton(assignment.Ssid + " → " + ProfileName(assignment.ProfileId));
                row.Click += (_, _) => EditAssignment(assignment, row);
                rows.Children.Add(row);
            }
        }
        void EditAssignment(WiFiProfileAssignment? existing, Button source)
        {
            var page = new StackPanel { Spacing = 12 };
            page.Children.Add(SectionLabel(existing is null ? "Add Wi-Fi assignment" : "Edit Wi-Fi assignment"));
            page.Children.Add(SecondaryText("Enter the exact Wi-Fi name, including any leading or trailing spaces."));
            var ssid = new TextBox
            {
                Header = "Wi-Fi name", Text = existing?.Ssid ?? string.Empty,
                Background = (Brush)Application.Current.Resources["NordicInsetBrush"],
                Foreground = (Brush)Application.Current.Resources["NordicPrimaryTextBrush"],
                BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
            };
            var picker = new ComboBox { Header = "Profile", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var profile in profiles)
                picker.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id });
            picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (Guid)i.Tag == existing?.ProfileId);
            var rowError = ModalErrorText();
            page.Children.Add(ssid);
            page.Children.Add(picker);
            page.Children.Add(rowError);
            var apply = RowButton(existing is null ? "Add assignment" : "Update assignment");
            apply.Click += (_, _) =>
            {
                try
                {
                    if (picker.SelectedItem is not ComboBoxItem selected) throw new ArgumentException("Choose a saved profile.");
                    var edited = new WiFiProfileAssignment(ssid.Text, (Guid)selected.Tag);
                    var updated = assignments.Where(a => a != existing).Append(edited).ToArray();
                    new AutomaticProfilePolicy { Assignments = updated, TrustedSsids = TrustedLines(trusted.Text) }.Validate();
                    assignments = updated.ToList();
                    RenderAssignments();
                    ShowForm();
                    add.Focus(FocusState.Programmatic);
                }
                catch (ArgumentException exception) { rowError.Text = exception.Message; rowError.Visibility = Visibility.Visible; }
            };
            page.Children.Add(apply);
            if (existing is not null)
            {
                var remove = RowButton("Remove this assignment");
                remove.Click += (_, _) => { assignments.Remove(existing); RenderAssignments(); ShowForm(); add.Focus(FocusState.Programmatic); };
                page.Children.Add(remove);
            }
            ShowSubpage(ModalCard(page), source);
            ssid.Focus(FocusState.Programmatic);
        }
        RenderAssignments();
        add.Click += (_, _) => EditAssignment(null, add);
        assignedSection.Children.Add(add);
        var access = RowButton("Wi-Fi name access…");
        access.Click += (_, _) =>
        {
            var page = new StackPanel { Spacing = 12 };
            page.Children.Add(SectionLabel("Wi-Fi name access"));
            page.Children.Add(SecondaryText("WireRoute reads only the connected Wi-Fi name; it does not scan nearby networks. Windows may restrict access based on Location privacy settings. If the name cannot be read and you have named Wi-Fi rules, automatic switching leaves the VPN unchanged instead of guessing. Wi-Fi names stay in protected settings on this PC."));
            var settings = RowButton("Open Windows Location settings");
            var accessError = ModalErrorText();
            settings.Click += async (_, _) =>
            {
                try
                {
                    if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-location")))
                        throw new InvalidOperationException("Open Windows Settings → Privacy & security → Location.");
                }
                catch (Exception exception) { accessError.Text = exception.Message; accessError.Visibility = Visibility.Visible; }
            };
            page.Children.Add(settings);
            page.Children.Add(accessError);
            ShowSubpage(ModalCard(page), access);
        };
        assignedSection.Children.Add(access);
        var control = Section("Your connection stays in your control", "Only profiles connected automatically by this running WireRoute app are switched. Manual control pauses switching until the physical network changes; manually connected profiles are never replaced. Turning this off leaves the current VPN connected. Restarting the app does not take ownership of an existing connection.");
        control.Children.Add(SecondaryText("Switching briefly disconnects the VPN and is not a kill switch. Windows may request administrator approval for each stop and start. Canceling or a failure pauses retries until the network changes or you save these rules. Rules run only while WireRoute is open in the tray, even with Persistent VPN enabled. They do not run across sign-out."));
        control.Children.Add(resumeLegacy);
        form.Children.Add(error);
        request = new ModalRequest
        {
            Title = "Automatic profiles", IconGlyph = "\uE701", Content = host,
            PrimaryText = "Save", CancelText = "Cancel", MaxWidth = 760,
            OnCancel = () =>
            {
                if (returnToForm is null) return Task.FromResult(true);
                returnToForm();
                return Task.FromResult(false);
            },
            OnPrimary = async () =>
            {
                try
                {
                    if (onDemandGate.CurrentCount == 0 || Profiles.Any(p => p.IsTransitioning))
                        throw new InvalidOperationException("Wait for the current tunnel operation to finish before saving rules.");
                    var policy = new AutomaticProfilePolicy
                    {
                        Enabled = enable.IsChecked == true, DefaultProfileId = defaultId,
                        WiFi = wifi, Cellular = cellular, Ethernet = ethernet,
                        TrustedSsids = TrustedLines(trusted.Text), Assignments = assignments.ToArray(),
                    };
                    policy.Validate();
                    var settings = appSettings with
                    {
                        AutomaticProfiles = policy,
                        SingleProfileOnDemandSuspended = policy.Enabled
                            || (appSettings.SingleProfileOnDemandSuspended && resumeLegacy.IsChecked != true),
                    };
                    await settingsStore.SaveAsync(settings, managerCancellation.Token);
                    appSettings = settings;
                    automaticSession.SettingsChanged(policy.Enabled);
                    nextOnDemandAttempt = DateTimeOffset.MinValue;
                    UpdateAutomaticProfilesStatus(policy.Enabled ? "Automatic profiles enabled. Waiting for a stable network…" : null);
                    return true;
                }
                catch (Exception exception) { return KeepModalOpen(request!, error, exception.Message); }
            },
        };
        ShowForm();
        if (await ShowModalAsync(request, parent) == WireRouteModalResult.Primary && parent is null)
            await EvaluateOnDemandAsync();
    }

    private static string[] TrustedLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n').Where(line => line.Length != 0).ToArray();
}
