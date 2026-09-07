using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WireRoute.App.Models;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private void UpdateProfileOnDemandSummary(ProfileNavigationItem? item)
    {
        var stored = item?.StoredProfile;
        ProfileOnDemandButton.IsEnabled = stored is not null && item?.IsManaged == false;
        var summary = stored is null ? "Off"
            : new ProfileOnDemandDraft(stored.OnDemandEthernet, stored.OnDemandWiFi)
                .Summary(appSettings.AutomaticProfiles.Enabled, appSettings.SingleProfileOnDemandSuspended);
        ProfileOnDemandText.Text = summary;
        ProfileOnDemandActionText.Text = summary == "Off" ? "Configure…" : summary + "…";
    }

    private async void ProfileOnDemandButton_Click(object sender, RoutedEventArgs e)
    {
        var item = selectedProfile;
        if (activeModal is not null || item?.StoredProfile is null || item.IsManaged) return;
        if (appSettings.AutomaticProfiles.Enabled)
        {
            await ShowAutomaticProfilesAsync();
            return;
        }

        var original = new ProfileOnDemandDraft(item.StoredProfile.OnDemandEthernet, item.StoredProfile.OnDemandWiFi);
        await ShowProfileOnDemandAsync(item.Name, original, onSave: async draft =>
        {
            // Only update rule metadata; never write a stale configuration over an edit.
            var current = item.StoredProfile;
            if (current is null || !Profiles.Contains(item))
                throw new InvalidOperationException("This profile is no longer available.");
            var updated = current with
            {
                OnDemandEthernet = draft.Ethernet, OnDemandWiFi = draft.WiFi,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await profileStore.SaveAsync(updated, managerCancellation.Token);
            item.UpdateStoredProfile(updated, item.Profile!);
            await ResumeLegacyOnDemandAsync(draft.ResumeSavedRules);
            UpdateProfileOnDemandSummary(item);
            await RecordActivityAsync(WireRouteActivityKind.ProfileUpdated, item, "Updated On-Demand rules.");
        });
        // A nested Automatic profiles Save may have changed mode even if this
        // single-profile dialog was canceled. Evaluate only after all dialogs close.
        await EvaluateOnDemandAsync();
    }

    private async Task ResumeLegacyOnDemandAsync(bool explicitlyRequested)
    {
        if (!explicitlyRequested || appSettings.AutomaticProfiles.Enabled || !appSettings.SingleProfileOnDemandSuspended)
            return;
        var resumed = appSettings with { SingleProfileOnDemandSuspended = false };
        await settingsStore.SaveAsync(resumed, managerCancellation.Token);
        appSettings = resumed;
        nextOnDemandAttempt = DateTimeOffset.MinValue;
        UpdateAutomaticProfilesStatus();
    }

    private async Task<ProfileOnDemandDraft?> ShowProfileOnDemandAsync(
        string profileName, ProfileOnDemandDraft initial, ModalRequest? parent = null,
        Func<ProfileOnDemandDraft, Task>? onSave = null)
    {
        if (appSettings.AutomaticProfiles.Enabled)
        {
            await ShowAutomaticProfilesAsync(parent);
            return null;
        }

        var ethernet = new CheckBox { Content = "Ethernet", IsChecked = initial.Ethernet };
        var wifi = new CheckBox { Content = "Wi-Fi", IsChecked = initial.WiFi };
        var resume = new CheckBox
        {
            Content = "Resume saved single-profile On-Demand rules", IsChecked = initial.ResumeSavedRules,
        };
        var status = SecondaryText(string.Empty);
        var error = ModalErrorText();
        var automatic = new Button
        {
            Content = "Switch profiles by network…", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 12, 16, 12),
        };
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(SecondaryText("Connect " + profileName + " automatically, or choose a different profile for each network. Saved single-profile rules are kept when you enable automatic profiles."));
        content.Children.Add(automatic);
        var rules = new StackPanel { Spacing = 10 };
        rules.Children.Add(SectionLabel("Connect this profile on"));
        rules.Children.Add(ethernet);
        rules.Children.Add(wifi);
        rules.Children.Add(status);
        rules.Children.Add(resume);
        content.Children.Add(ModalCard(rules));
        content.Children.Add(SecondaryText("Rules run while WireRoute is open in the tray. Saving automatic profiles in the next screen is independent of saving this profile's rules or configuration."));
        content.Children.Add(error);
        void RefreshMode()
        {
            var enabled = appSettings.AutomaticProfiles.Enabled;
            ethernet.IsEnabled = wifi.IsEnabled = !enabled;
            resume.IsEnabled = !enabled;
            resume.Visibility = appSettings.SingleProfileOnDemandSuspended ? Visibility.Visible : Visibility.Collapsed;
            if (enabled) resume.IsChecked = false;
            status.Text = enabled ? "Automatic profile switching is enabled. These saved rules are paused."
                : appSettings.SingleProfileOnDemandSuspended
                    ? "Saved rules are paused. Resuming applies to all saved single-profile rules."
                    : "Leave both unchecked to turn single-profile On-Demand off.";
            automatic.Content = enabled ? "Automatic profile switching…" : "Switch profiles by network…";
        }
        ProfileOnDemandDraft? result = null;
        ModalRequest? request = null;
        request = new ModalRequest
        {
            Title = "On-Demand", IconGlyph = "\uE701", Content = content,
            PrimaryText = "Save", CancelText = "Cancel", MaxWidth = 700,
            OnPrimary = async () =>
            {
                try
                {
                    if (onDemandGate.CurrentCount == 0 || Profiles.Any(p => p.IsTransitioning))
                        throw new InvalidOperationException("Wait for the current tunnel operation to finish before saving rules.");
                    var draft = new ProfileOnDemandDraft(ethernet.IsChecked == true, wifi.IsChecked == true,
                        resume.IsChecked == true && !appSettings.AutomaticProfiles.Enabled);
                    if (onSave is not null) await onSave(draft);
                    result = draft;
                    return true;
                }
                catch (Exception exception) { return KeepModalOpen(request!, error, exception.Message); }
            },
        };
        automatic.Click += async (_, _) =>
        {
            if (activeModal != request || request.IsBusy) return;
            var previousPolicy = appSettings.AutomaticProfiles;
            try
            {
                await ShowAutomaticProfilesAsync(request);
                if (!ReferenceEquals(previousPolicy, appSettings.AutomaticProfiles)) resume.IsChecked = false;
            }
            catch (Exception exception) { KeepModalOpen(request, error, exception.Message); }
            RefreshMode();
        };
        RefreshMode();
        return await ShowModalAsync(request, parent) == WireRouteModalResult.Primary ? result : null;
    }
}
