using System.Runtime.Versioning;

namespace WireRoute.Storage;

public sealed record WireRouteAppSettings(
    string Theme,
    string TrayIconStyle,
    string PreferredEndpoint,
    string DnsServers,
    string SplitTunnelRoutes,
    int PersistentKeepalive,
    bool PersistentTunnelService,
    int ActivityRetentionDays = 7)
{
    public static WireRouteAppSettings Defaults { get; } = new(
        "Blue Nordic",
        "Default",
        string.Empty,
        string.Empty,
        string.Empty,
        25,
        false,
        7);
}

[SupportedOSPlatform("windows")]
public sealed class WireRouteSettingsStore
{
    private readonly ProtectedJsonFile<WireRouteAppSettings> settings;

    public WireRouteSettingsStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? WireRouteStoragePaths.DefaultDirectory;
        settings = new ProtectedJsonFile<WireRouteAppSettings>(
            Path.Combine(directory, "settings.dpapi"),
            () => WireRouteAppSettings.Defaults);
    }

    public Task<WireRouteAppSettings> LoadAsync(
        CancellationToken cancellationToken = default) =>
        settings.ReadAsync(cancellationToken);

    public async Task SaveAsync(
        WireRouteAppSettings value,
        CancellationToken cancellationToken = default)
    {
        Validate(value);
        _ = await settings.UpdateAsync(
            _ => value,
            cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(WireRouteAppSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Theme is not ("Blue Nordic" or "System"))
        {
            throw new ArgumentException("Choose a supported WireRoute theme.", nameof(value));
        }
        if (string.IsNullOrWhiteSpace(value.TrayIconStyle))
        {
            throw new ArgumentException("Choose a Windows tray icon style.", nameof(value));
        }
        if (value.PersistentKeepalive is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentException(
                "Keepalive must be between 0 and 65535 seconds.",
                nameof(value));
        }
        if (value.ActivityRetentionDays is not (1 or 7 or 30))
        {
            throw new ArgumentException(
                "Activity retention must be 1, 7, or 30 days.",
                nameof(value));
        }
    }
}
