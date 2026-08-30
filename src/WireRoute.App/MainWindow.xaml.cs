using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WireRoute.App.Models;

namespace WireRoute.App;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow appWindow;

    public MainWindow()
    {
        InitializeComponent();
        Profiles.CollectionChanged += (_, _) => UpdateProfilesEmptyState();

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        appWindow = AppWindow.GetFromWindowId(windowId);
        ConfigureWindow();
        UpdateProfilesEmptyState();
    }

    public ObservableCollection<ProfileNavigationItem> Profiles { get; } = [];

    private void ConfigureWindow()
    {
        appWindow.Resize(new SizeInt32(1180, 760));
        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 243, 247, 252);
            titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 127, 146, 168);
            titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 33, 50, 72);
            titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 53, 74, 98);
        }
    }

    private void UpdateProfilesEmptyState()
    {
        ProfilesEmptyState.Visibility = Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RouterOSButton_Click(object sender, RoutedEventArgs e) => ShowDestination(Destination.RouterOS);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowDestination(Destination.Settings);

    private void ShowDestination(Destination destination)
    {
        ProfileEmptyPanel.Visibility = destination == Destination.Profile ? Visibility.Visible : Visibility.Collapsed;
        RouterOSPanel.Visibility = destination == Destination.RouterOS ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = destination == Destination.Settings ? Visibility.Visible : Visibility.Collapsed;

        SetSelectedState(RouterOSButton, RouterOSRail, destination == Destination.RouterOS);
        SetSelectedState(SettingsButton, SettingsRail, destination == Destination.Settings);
    }

    private static void SetSelectedState(Button button, Border rail, bool isSelected)
    {
        rail.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        button.Background = isSelected
            ? new SolidColorBrush(ColorHelper.FromArgb(41, 76, 131, 243))
            : new SolidColorBrush(Colors.Transparent);
        button.Foreground = isSelected
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 76, 131, 243))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 243, 247, 252));
    }

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "A native Windows client for clear, protected WireGuard routing.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Copyright © 2026 WireRoute contributors.\nPortions © 2018–2023 WireGuard LLC.",
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "About WireRoute",
            Content = content,
            Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
            BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
            BorderThickness = new Thickness(1),
            CloseButtonText = "Done",
            CloseButtonStyle = (Style)Application.Current.Resources["NordicAccentButtonStyle"],
            DefaultButton = ContentDialogButton.None,
        };
        await dialog.ShowAsync();
    }

    private enum Destination
    {
        Profile,
        RouterOS,
        Settings,
    }
}
