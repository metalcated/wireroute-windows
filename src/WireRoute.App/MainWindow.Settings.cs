using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WireRoute.Core.Routing;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private readonly WireRouteSettingsStore settingsStore = new();
    private WireRouteAppSettings appSettings = WireRouteAppSettings.Defaults;

    private async Task LoadSettingsAsync()
    {
        try
        {
            appSettings = await settingsStore.LoadAsync(managerCancellation.Token);
            PopulateSettingsFields(appSettings);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Settings are unavailable", exception.Message);
        }
    }

    private void PopulateSettingsFields(WireRouteAppSettings settings)
    {
        SelectComboItem(SettingsThemePicker, settings.Theme);
        SelectComboItem(SettingsTrayIconPicker, settings.TrayIconStyle);
        SettingsPreferredEndpointBox.Text = settings.PreferredEndpoint;
        SettingsDnsServersBox.Text = settings.DnsServers;
        SettingsSplitRoutesBox.Text = settings.SplitTunnelRoutes;
        SettingsKeepaliveBox.Value = settings.PersistentKeepalive;
    }

    private async void SettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preferredEndpoint = SettingsPreferredEndpointBox.Text.Trim();
            if (preferredEndpoint.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException(
                    "The preferred endpoint cannot contain spaces.");
            }
            var dnsServers = ParseCsv(SettingsDnsServersBox.Text);
            if (dnsServers.Any(value => !IPAddress.TryParse(value, out _)))
            {
                throw new ArgumentException(
                    "DNS servers must be valid IPv4 or IPv6 addresses.");
            }
            var splitRoutes = ParseCsv(SettingsSplitRoutesBox.Text);
            _ = splitRoutes.Select(value => new RoutePrefix(value)).ToArray();
            var keepalive = (int)SettingsKeepaliveBox.Value;
            var settings = new WireRouteAppSettings(
                SelectedComboText(SettingsThemePicker, "Blue Nordic"),
                SelectedComboText(SettingsTrayIconPicker, "Default"),
                preferredEndpoint,
                string.Join(", ", dnsServers),
                string.Join(", ", splitRoutes),
                keepalive);
            await settingsStore.SaveAsync(settings, managerCancellation.Token);
            appSettings = settings;
            await ShowMessageAsync(
                "Settings saved",
                "New RouterOS client profiles will use these defaults.");
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or RoutePrefixValidationException
            or WireRouteStorageException)
        {
            await ShowMessageAsync("Settings could not be saved", exception.Message);
        }
    }

    private void SettingsRestoreDefaultsButton_Click(object sender, RoutedEventArgs e) =>
        PopulateSettingsFields(WireRouteAppSettings.Defaults);

    private static string[] ParseCsv(string value) =>
        value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SelectedComboText(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static void SelectComboItem(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                item.Content?.ToString()?.Equals(
                    value,
                    StringComparison.OrdinalIgnoreCase) == true)
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }
}
