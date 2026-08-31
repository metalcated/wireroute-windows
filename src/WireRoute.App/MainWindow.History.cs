using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WireRoute.App.Interop;
using WireRoute.App.Models;
using WireRoute.Storage;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private async Task ShowProfileHistoryAsync(ProfileNavigationItem item)
    {
        if (item.StoredProfile is null)
        {
            await ShowMessageAsync(
                "Activity history unavailable",
                "Connection history is available for profiles stored securely by WireRoute.");
            return;
        }

        var profile = item.StoredProfile;
        var downloadValue = HistoryMetricValue("Zero KB/s");
        var uploadValue = HistoryMetricValue("Zero KB/s");
        var totalValue = HistoryMetricValue("—");
        var handshakeValue = HistoryMetricValue("—");
        var stateValue = SecondaryText("Previous connection activity");
        stateValue.FontSize = 11;
        var chartMessage = SecondaryText("Connect this profile to begin recording traffic.");
        chartMessage.HorizontalAlignment = HorizontalAlignment.Center;
        chartMessage.VerticalAlignment = VerticalAlignment.Center;
        chartMessage.FontSize = 12;
        var downloadLine = new Polyline
        {
            Stroke = (Brush)Application.Current.Resources["NordicAccentBrush"],
            StrokeLineJoin = PenLineJoin.Round,
            StrokeThickness = 2,
        };
        var uploadLine = new Polyline
        {
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 211, 209)),
            StrokeLineJoin = PenLineJoin.Round,
            StrokeThickness = 2,
        };
        var chart = new Grid { Height = 82 };
        chart.Children.Add(HistoryGridLine(VerticalAlignment.Top));
        chart.Children.Add(HistoryGridLine(VerticalAlignment.Center));
        chart.Children.Add(HistoryGridLine(VerticalAlignment.Bottom));
        chart.Children.Add(downloadLine);
        chart.Children.Add(uploadLine);
        chart.Children.Add(chartMessage);

        var metrics = new Grid { ColumnSpacing = 24 };
        for (var index = 0; index < 4; index++)
        {
            metrics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }
        metrics.Children.Add(HistoryMetric("Download", downloadValue, "NordicAccentBrush", 0));
        metrics.Children.Add(HistoryMetric("Upload", uploadValue, null, 1));
        metrics.Children.Add(HistoryMetric("Session total", totalValue, "NordicSecondaryTextBrush", 2));
        metrics.Children.Add(HistoryMetric("Last handshake", handshakeValue, "NordicSecondaryTextBrush", 3));

        var dashboardHeader = new StackPanel { Spacing = 2 };
        dashboardHeader.Children.Add(new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Activity",
        });
        dashboardHeader.Children.Add(stateValue);
        var dashboard = new StackPanel { Spacing = 10 };
        dashboard.Children.Add(dashboardHeader);
        dashboard.Children.Add(metrics);
        dashboard.Children.Add(chart);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        legend.Children.Add(HistoryLegend("Download", "NordicAccentBrush"));
        legend.Children.Add(HistoryLegend("Upload", null));
        dashboard.Children.Add(legend);
        var dashboardCard = ModalCard(dashboard);
        dashboardCard.CornerRadius = new CornerRadius(14);

        var historyStack = new StackPanel { Spacing = 0, MinHeight = 145 };
        var historyCard = ModalCard(historyStack);
        historyCard.CornerRadius = new CornerRadius(14);
        historyCard.Padding = new Thickness(16, 8, 16, 8);

        var retentionPicker = new ComboBox { MinWidth = 96 };
        retentionPicker.Items.Add(new ComboBoxItem { Content = "1 day", Tag = 1 });
        retentionPicker.Items.Add(new ComboBoxItem { Content = "7 days", Tag = 7 });
        retentionPicker.Items.Add(new ComboBoxItem { Content = "30 days", Tag = 30 });
        retentionPicker.SelectedIndex = appSettings.ActivityRetentionDays switch
        {
            1 => 0,
            30 => 2,
            _ => 1,
        };
        var clearButton = new Button { Content = "Clear previous activity…" };
        var historyHeader = new Grid { ColumnSpacing = 10 };
        historyHeader.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        historyHeader.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Recent connections",
        });
        Grid.SetColumn(retentionPicker, 1);
        historyHeader.Children.Add(retentionPicker);
        Grid.SetColumn(clearButton, 2);
        historyHeader.Children.Add(clearButton);

        var clearError = ModalErrorText();
        var clearConfirmation = new Border
        {
            Background = (Brush)Application.Current.Resources["NordicRaisedBrush"],
            BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Visibility = Visibility.Collapsed,
        };
        var confirmationGrid = new Grid { ColumnSpacing = 10 };
        confirmationGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        confirmationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        confirmationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var confirmationText = new StackPanel { Spacing = 3 };
        confirmationText.Children.Add(new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = "Clear previous activity?",
        });
        confirmationText.Children.Add(SecondaryText(
            "WireRoute will remove completed connection history for this profile. The current connection will continue recording."));
        confirmationGrid.Children.Add(confirmationText);
        var cancelClearButton = new Button { Content = "Cancel" };
        Grid.SetColumn(cancelClearButton, 1);
        confirmationGrid.Children.Add(cancelClearButton);
        var confirmClearButton = new Button
        {
            Content = "Clear Activity",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        };
        Grid.SetColumn(confirmClearButton, 2);
        confirmationGrid.Children.Add(confirmClearButton);
        clearConfirmation.Child = confirmationGrid;

        var content = new StackPanel { Spacing = 14, MinHeight = 490 };
        content.Children.Add(dashboardCard);
        content.Children.Add(historyHeader);
        content.Children.Add(clearConfirmation);
        content.Children.Add(historyCard);
        content.Children.Add(clearError);

        var historySamples = new Queue<ActivityRateSample>();
        WireGuardTunnelMetrics? previous = null;
        var previousAt = default(DateTimeOffset);
        var isRefreshing = false;
        var lastSessionsRefresh = default(DateTimeOffset);

        async Task RefreshSessionsAsync()
        {
            var sessions = await activityStore.LoadConnectionSessionsAsync(
                profile.Id,
                8,
                managerCancellation.Token);
            historyStack.Children.Clear();
            if (sessions.Count == 0)
            {
                historyStack.Children.Add(SecondaryText("No connection history yet."));
                return;
            }
            for (var index = 0; index < sessions.Count; index++)
            {
                historyStack.Children.Add(HistorySessionRow(
                    sessions[index],
                    index < sessions.Count - 1));
            }
        }

        async Task RefreshAsync(bool forceSessions = false)
        {
            if (isRefreshing)
            {
                return;
            }
            isRefreshing = true;
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (item.IsActive
                    && localTunnelController.TryReadMetrics(profile, out var live, out _)
                    && live is not null)
                {
                    var elapsed = previous is null
                        ? 0
                        : Math.Max(0.001, (now - previousAt).TotalSeconds);
                    var downloadRate = previous is null || live.ReceivedBytes < previous.ReceivedBytes
                        ? 0
                        : (live.ReceivedBytes - previous.ReceivedBytes) / elapsed;
                    var uploadRate = previous is null || live.SentBytes < previous.SentBytes
                        ? 0
                        : (live.SentBytes - previous.SentBytes) / elapsed;
                    previous = live;
                    previousAt = now;
                    downloadValue.Text = FormatRate(downloadRate);
                    uploadValue.Text = FormatRate(uploadRate);
                    totalValue.Text = FormatBytes(SaturatingTotal(live.ReceivedBytes, live.SentBytes));
                    handshakeValue.Text = live.LastHandshake is null
                        ? "—"
                        : FormatElapsed(now - live.LastHandshake.Value);
                    stateValue.Text = "Recording this connection";
                    chartMessage.Text = live.LastHandshake is null
                        ? "Waiting for the first WireGuard handshake."
                        : string.Empty;
                    chartMessage.Visibility = live.LastHandshake is null
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    historySamples.Enqueue(new ActivityRateSample(downloadRate, uploadRate));
                    while (historySamples.Count > MaximumActivitySamples)
                    {
                        _ = historySamples.Dequeue();
                    }
                    UpdateHistoryChart(downloadLine, uploadLine, historySamples);
                }
                else
                {
                    downloadValue.Text = "Zero KB/s";
                    uploadValue.Text = "Zero KB/s";
                    stateValue.Text = "Previous connection activity";
                    if (historySamples.Count == 0)
                    {
                        chartMessage.Text = "Connect this profile to begin recording traffic.";
                        chartMessage.Visibility = Visibility.Visible;
                    }
                }

                if (forceSessions || now - lastSessionsRefresh >= TimeSpan.FromSeconds(2))
                {
                    await RefreshSessionsAsync();
                    var latest = (await activityStore.LoadConnectionSessionsAsync(
                        profile.Id,
                        1,
                        managerCancellation.Token)).FirstOrDefault();
                    if (!item.IsActive && latest is not null)
                    {
                        totalValue.Text = FormatBytes(SaturatingTotal(
                            latest.ReceivedBytes,
                            latest.SentBytes));
                        handshakeValue.Text = latest.LastHandshake is null
                            ? "—"
                            : FormatElapsed(now - latest.LastHandshake.Value);
                    }
                    lastSessionsRefresh = now;
                }
                clearError.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception) when (
                exception is WireRouteStorageException or OperationCanceledException)
            {
                clearError.Text = exception.Message;
                clearError.Visibility = Visibility.Visible;
            }
            finally
            {
                isRefreshing = false;
            }
        }

        retentionPicker.SelectionChanged += async (_, _) =>
        {
            if (retentionPicker.SelectedItem is not ComboBoxItem choice
                || choice.Tag is not int retentionDays
                || retentionDays == appSettings.ActivityRetentionDays)
            {
                return;
            }
            try
            {
                appSettings = appSettings with { ActivityRetentionDays = retentionDays };
                await settingsStore.SaveAsync(appSettings, managerCancellation.Token);
                await activityStore.PurgeConnectionSessionsAsync(
                    DateTimeOffset.UtcNow.AddDays(-retentionDays),
                    managerCancellation.Token);
                await RefreshAsync(forceSessions: true);
            }
            catch (Exception exception) when (
                exception is WireRouteStorageException or OperationCanceledException)
            {
                clearError.Text = exception.Message;
                clearError.Visibility = Visibility.Visible;
            }
        };
        clearButton.Click += (_, _) => clearConfirmation.Visibility = Visibility.Visible;
        cancelClearButton.Click += (_, _) => clearConfirmation.Visibility = Visibility.Collapsed;
        confirmClearButton.Click += async (_, _) =>
        {
            try
            {
                await activityStore.ClearCompletedConnectionSessionsAsync(
                    profile.Id,
                    managerCancellation.Token);
                clearConfirmation.Visibility = Visibility.Collapsed;
                await RefreshAsync(forceSessions: true);
            }
            catch (Exception exception) when (
                exception is WireRouteStorageException or OperationCanceledException)
            {
                clearError.Text = exception.Message;
                clearError.Visibility = Visibility.Visible;
            }
        };

        await activityStore.PurgeConnectionSessionsAsync(
            DateTimeOffset.UtcNow.AddDays(-appSettings.ActivityRetentionDays),
            managerCancellation.Token);
        await RefreshAsync(forceSessions: true);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        try
        {
            await ShowModalAsync(new ModalRequest
            {
                Title = "Activity",
                Subtitle = "See live transfer rates and recent connection history for this profile. Activity stays on this device.",
                HeaderActionText = "Done",
                Content = content,
                CancelText = null,
                MaxWidth = 760,
            });
        }
        finally
        {
            timer.Stop();
        }
    }

    private static TextBlock HistoryMetricValue(string text) => new()
    {
        FontFamily = new FontFamily("Cascadia Mono"),
        FontSize = 15,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Text = text,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static StackPanel HistoryMetric(
        string title,
        TextBlock value,
        string? brushKey,
        int column)
    {
        var titleText = new TextBlock
        {
            Foreground = brushKey is null
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 211, 209))
                : (Brush)Application.Current.Resources[brushKey],
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = title,
        };
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(titleText);
        stack.Children.Add(value);
        Grid.SetColumn(stack, column);
        return stack;
    }

    private static Rectangle HistoryGridLine(VerticalAlignment alignment) => new()
    {
        Height = 1,
        VerticalAlignment = alignment,
        Fill = (Brush)Application.Current.Resources["NordicBorderBrush"],
    };

    private static StackPanel HistoryLegend(string title, string? brushKey)
    {
        var color = brushKey is null
            ? Windows.UI.Color.FromArgb(255, 25, 211, 209)
            : ((SolidColorBrush)Application.Current.Resources[brushKey]).Color;
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        legend.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(color),
        });
        legend.Children.Add(new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            FontSize = 10,
            Text = title,
        });
        return legend;
    }

    private static FrameworkElement HistorySessionRow(
        WireRouteConnectionSession session,
        bool showsSeparator)
    {
        var started = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = session.StartedAt.ToLocalTime().ToString("g"),
        };
        var transfer = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 11,
            Text = $"↓ {FormatBytes(session.ReceivedBytes)}   ↑ {FormatBytes(session.SentBytes)}",
        };
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(started);
        text.Children.Add(transfer);
        var row = new Grid { Padding = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        var duration = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["NordicSecondaryTextBrush"],
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 11,
            Text = FormatHistoryDuration(session),
        };
        Grid.SetColumn(duration, 1);
        row.Children.Add(duration);
        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["NordicBorderBrush"],
            BorderThickness = showsSeparator ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            Child = row,
        };
    }

    private static void UpdateHistoryChart(
        Polyline downloadLine,
        Polyline uploadLine,
        IEnumerable<ActivityRateSample> values)
    {
        var samples = values.ToArray();
        var width = Math.Max(1, downloadLine.ActualWidth);
        var height = Math.Max(1, downloadLine.ActualHeight);
        var maximum = Math.Max(
            1024,
            samples.Select(sample => Math.Max(sample.Download, sample.Upload)).DefaultIfEmpty().Max());
        downloadLine.Points.Clear();
        uploadLine.Points.Clear();
        for (var index = 0; index < samples.Length; index++)
        {
            var x = samples.Length == 1 ? width : index * width / (samples.Length - 1);
            downloadLine.Points.Add(new Point(
                x,
                height - samples[index].Download / maximum * height));
            uploadLine.Points.Add(new Point(
                x,
                height - samples[index].Upload / maximum * height));
        }
    }

    private static string FormatHistoryDuration(WireRouteConnectionSession session)
    {
        var duration = (session.EndedAt ?? DateTimeOffset.Now) - session.StartedAt;
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }
        if (duration < TimeSpan.FromHours(1))
        {
            return $"{Math.Floor(duration.TotalMinutes)}m";
        }
        return $"{Math.Floor(duration.TotalHours)}h {duration.Minutes}m";
    }

    private static ulong SaturatingTotal(ulong first, ulong second) =>
        ulong.MaxValue - first < second ? ulong.MaxValue : first + second;
}
