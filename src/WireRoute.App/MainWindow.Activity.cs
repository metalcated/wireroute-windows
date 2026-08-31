using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using WireRoute.App.Interop;

namespace WireRoute.App;

public sealed partial class MainWindow
{
    private const int MaximumActivitySamples = 60;
    private readonly Queue<ActivityRateSample> activitySamples = new();
    private DispatcherQueueTimer? activityTimer;
    private string? activityProfileName;
    private WireGuardTunnelMetrics? previousMetrics;
    private DateTimeOffset previousMetricsAt;

    private void StartActivityMonitoring()
    {
        activityTimer = DispatcherQueue.CreateTimer();
        activityTimer.Interval = TimeSpan.FromSeconds(1);
        activityTimer.IsRepeating = true;
        activityTimer.Tick += (_, _) => UpdateLiveActivity();
        activityTimer.Start();
    }

    private void StopActivityMonitoring()
    {
        activityTimer?.Stop();
        activityTimer = null;
        ResetLiveActivity();
    }

    private void UpdateLiveActivity()
    {
        var item = selectedProfile;
        if (item is null || !item.IsActive)
        {
            if (activityProfileName is not null)
            {
                ResetLiveActivity();
            }
            return;
        }

        var adapterName = item.ManagerName ?? item.Name;
        if (!activityProfileName?.Equals(adapterName, StringComparison.OrdinalIgnoreCase) ?? true)
        {
            ResetLiveActivity();
            activityProfileName = adapterName;
        }

        var readMetrics = item.StoredProfile is not null
            ? localTunnelController.TryReadMetrics(item.StoredProfile, out var metrics, out var error)
            : WireGuardRuntimeMetrics.TryRead(adapterName, out metrics, out error);
        if (!readMetrics
            || metrics is null)
        {
            ProfileActivityText.Visibility = Visibility.Visible;
            ProfileActivityText.Text = "Tunnel active. Live WireGuardNT counters are unavailable"
                + (string.IsNullOrWhiteSpace(error) ? "." : ": " + error);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = previousMetrics is null
            ? 0
            : Math.Max(0.001, (now - previousMetricsAt).TotalSeconds);
        var downloadRate = previousMetrics is null || metrics.ReceivedBytes < previousMetrics.ReceivedBytes
            ? 0
            : (metrics.ReceivedBytes - previousMetrics.ReceivedBytes) / elapsed;
        var uploadRate = previousMetrics is null || metrics.SentBytes < previousMetrics.SentBytes
            ? 0
            : (metrics.SentBytes - previousMetrics.SentBytes) / elapsed;
        previousMetrics = metrics;
        previousMetricsAt = now;

        ProfileDownloadText.Text = FormatRate(downloadRate);
        ProfileUploadText.Text = FormatRate(uploadRate);
        ProfileSessionTotalText.Text = FormatBytes(metrics.ReceivedBytes + metrics.SentBytes);
        ProfileHandshakeText.Text = metrics.LastHandshake is null
            ? "—"
            : FormatElapsed(now - metrics.LastHandshake.Value);
        ProfileActivityText.Visibility = metrics.LastHandshake is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfileActivityText.Text = metrics.LastHandshake is null
            ? "Waiting for the first WireGuard handshake."
            : string.Empty;

        activitySamples.Enqueue(new ActivityRateSample(downloadRate, uploadRate));
        while (activitySamples.Count > MaximumActivitySamples)
        {
            _ = activitySamples.Dequeue();
        }
        UpdateActivityChart();
    }

    private void ResetLiveActivity()
    {
        activityProfileName = null;
        previousMetrics = null;
        previousMetricsAt = default;
        activitySamples.Clear();
        ProfileDownloadLine.Points.Clear();
        ProfileUploadLine.Points.Clear();
    }

    private void UpdateActivityChart()
    {
        var width = Math.Max(1, ProfileDownloadLine.ActualWidth);
        var height = Math.Max(1, ProfileDownloadLine.ActualHeight);
        var samples = activitySamples.ToArray();
        var maximum = Math.Max(
            1024,
            samples.Select(sample => Math.Max(sample.Download, sample.Upload)).DefaultIfEmpty().Max());
        ProfileDownloadLine.Points.Clear();
        ProfileUploadLine.Points.Clear();
        for (var index = 0; index < samples.Length; index++)
        {
            var x = samples.Length == 1
                ? width
                : index * width / (samples.Length - 1);
            ProfileDownloadLine.Points.Add(new Point(
                x,
                height - samples[index].Download / maximum * height));
            ProfileUploadLine.Points.Add(new Point(
                x,
                height - samples[index].Upload / maximum * height));
        }
    }

    private static string FormatRate(double bytesPerSecond) =>
        FormatBytes((ulong)Math.Max(0, bytesPerSecond)) + "/s";

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        if (elapsed.TotalSeconds < 60)
        {
            return $"{Math.Floor(elapsed.TotalSeconds)}s ago";
        }
        if (elapsed.TotalMinutes < 60)
        {
            return $"{Math.Floor(elapsed.TotalMinutes)}m ago";
        }
        if (elapsed.TotalHours < 24)
        {
            return $"{Math.Floor(elapsed.TotalHours)}h ago";
        }
        return $"{Math.Floor(elapsed.TotalDays)}d ago";
    }

    private readonly record struct ActivityRateSample(double Download, double Upload);
}
