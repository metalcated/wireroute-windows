using System.Net;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
            ApplyAppearanceSettings(appSettings);
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
            ApplyAppearanceSettings(settings);
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

    private void Root_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (appSettings.Theme.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            ApplySystemPalette();
        }
    }

    private void ApplyAppearanceSettings(WireRouteAppSettings settings)
    {
        var followsSystem = settings.Theme.Equals("System", StringComparison.OrdinalIgnoreCase);
        Root.RequestedTheme = followsSystem ? ElementTheme.Default : ElementTheme.Dark;
        if (followsSystem)
        {
            _ = DispatcherQueue.TryEnqueue(ApplySystemPalette);
        }
        else
        {
            ApplyBlueNordicPalette();
        }
    }

    private void ApplySystemPalette()
    {
        if (Root.ActualTheme == ElementTheme.Light)
        {
            ApplyPalette(
                ColorHelper.FromArgb(255, 245, 247, 250),
                ColorHelper.FromArgb(255, 238, 242, 247),
                ColorHelper.FromArgb(255, 241, 244, 248),
                Colors.White,
                ColorHelper.FromArgb(255, 229, 234, 241),
                ColorHelper.FromArgb(255, 197, 207, 219),
                ColorHelper.FromArgb(255, 23, 34, 52),
                ColorHelper.FromArgb(255, 83, 101, 122),
                ColorHelper.FromArgb(255, 113, 129, 151));
            return;
        }

        ApplyPalette(
            ColorHelper.FromArgb(255, 32, 32, 32),
            ColorHelper.FromArgb(255, 25, 25, 25),
            ColorHelper.FromArgb(255, 38, 38, 38),
            ColorHelper.FromArgb(255, 43, 43, 43),
            ColorHelper.FromArgb(255, 52, 52, 52),
            ColorHelper.FromArgb(255, 70, 70, 70),
            ColorHelper.FromArgb(255, 250, 250, 250),
            ColorHelper.FromArgb(255, 190, 190, 190),
            ColorHelper.FromArgb(255, 148, 148, 148));
    }

    private void ApplyBlueNordicPalette() => ApplyPalette(
        ColorHelper.FromArgb(255, 17, 27, 42),
        ColorHelper.FromArgb(255, 16, 26, 40),
        ColorHelper.FromArgb(255, 20, 34, 53),
        ColorHelper.FromArgb(255, 24, 38, 56),
        ColorHelper.FromArgb(255, 33, 50, 72),
        ColorHelper.FromArgb(255, 53, 74, 98),
        ColorHelper.FromArgb(255, 243, 247, 252),
        ColorHelper.FromArgb(255, 169, 184, 202),
        ColorHelper.FromArgb(255, 127, 146, 168));

    private void ApplyPalette(
        Windows.UI.Color canvas,
        Windows.UI.Color sidebar,
        Windows.UI.Color inset,
        Windows.UI.Color surface,
        Windows.UI.Color raised,
        Windows.UI.Color border,
        Windows.UI.Color primary,
        Windows.UI.Color secondary,
        Windows.UI.Color tertiary)
    {
        SetBrushColor("NordicCanvasBrush", canvas);
        SetBrushColor("NordicSidebarBrush", sidebar);
        SetBrushColor("NordicInsetBrush", inset);
        SetBrushColor("NordicSurfaceBrush", surface);
        SetBrushColor("NordicRaisedBrush", raised);
        SetBrushColor("NordicBorderBrush", border);
        SetBrushColor("NordicPrimaryTextBrush", primary);
        SetBrushColor("NordicSecondaryTextBrush", secondary);
        SetBrushColor("NordicTertiaryTextBrush", tertiary);

        var titleBar = appWindow.TitleBar;
        titleBar.ButtonForegroundColor = primary;
        titleBar.ButtonInactiveForegroundColor = tertiary;
        titleBar.ButtonHoverBackgroundColor = raised;
        titleBar.ButtonPressedBackgroundColor = border;
    }

    private static void SetBrushColor(string key, Windows.UI.Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

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
