using System.Runtime.Versioning;

namespace WireRoute.Storage;

public enum RouterOSProfileRecoveryReason
{
    PendingRouterWrite,
    RouterWriteUncertain,
    ManagerImportFailed,
    PendingRouterKeyReplacement,
    RouterKeyReplacementUncertain,
}

public sealed record RouterOSProfileRecovery(
    Guid Id,
    string DisplayName,
    string WgQuickConfiguration,
    DateTimeOffset CreatedAt,
    RouterOSProfileRecoveryReason Reason)
{
    public Guid? RouterConnectionId { get; init; }

    public string? RouterPeerId { get; init; }

    public string? OriginalPeerPublicKey { get; init; }

    public string? ReplacementPublicKey { get; init; }

    public Guid? ProfileId { get; init; }

    public string? TunnelName { get; init; }

    public bool IsPeerKeyReplacement =>
        Reason is RouterOSProfileRecoveryReason.PendingRouterKeyReplacement
            or RouterOSProfileRecoveryReason.RouterKeyReplacementUncertain
        || (Reason == RouterOSProfileRecoveryReason.ManagerImportFailed
            && RouterConnectionId is not null);
}

[SupportedOSPlatform("windows")]
public sealed class RouterOSProfileRecoveryStore
{
    private readonly ProtectedJsonFile<List<RouterOSProfileRecovery>> recoveries;

    public RouterOSProfileRecoveryStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? WireRouteStoragePaths.DefaultDirectory;
        recoveries = new ProtectedJsonFile<List<RouterOSProfileRecovery>>(
            Path.Combine(directory, "routeros-profile-recovery.dpapi"),
            () => []);
    }

    public async Task<IReadOnlyList<RouterOSProfileRecovery>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        (await recoveries.ReadAsync(cancellationToken).ConfigureAwait(false)).ToArray();

    public async Task SaveAsync(
        RouterOSProfileRecovery recovery,
        CancellationToken cancellationToken = default)
    {
        Validate(recovery);
        _ = await recoveries.UpdateAsync(current =>
        {
            var updated = current.Where(value => value.Id != recovery.Id).ToList();
            updated.Add(recovery);
            return updated;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RouterOSProfileRecovery?> LoadForPeerAsync(
        Guid routerConnectionId,
        string routerPeerId,
        CancellationToken cancellationToken = default)
    {
        if (routerConnectionId == Guid.Empty)
        {
            throw new ArgumentException("A RouterOS connection identifier is required.", nameof(routerConnectionId));
        }
        if (string.IsNullOrWhiteSpace(routerPeerId))
        {
            throw new ArgumentException("A RouterOS peer identifier is required.", nameof(routerPeerId));
        }

        return (await LoadAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(value => value.IsPeerKeyReplacement
                && value.RouterConnectionId == routerConnectionId
                && value.RouterPeerId!.Equals(routerPeerId, StringComparison.Ordinal))
            .OrderByDescending(value => value.CreatedAt)
            .FirstOrDefault();
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _ = await recoveries.UpdateAsync(
            current => current.Where(value => value.Id != id).ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(RouterOSProfileRecovery recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.Id == Guid.Empty)
        {
            throw new ArgumentException("A recovery identifier is required.", nameof(recovery));
        }

        if (string.IsNullOrWhiteSpace(recovery.DisplayName))
        {
            throw new ArgumentException("A recovery profile name is required.", nameof(recovery));
        }

        if (string.IsNullOrWhiteSpace(recovery.WgQuickConfiguration))
        {
            throw new ArgumentException("A recovery configuration is required.", nameof(recovery));
        }

        if (recovery.IsPeerKeyReplacement
            && (recovery.RouterConnectionId is not { } connectionId
                || connectionId == Guid.Empty
                || string.IsNullOrWhiteSpace(recovery.RouterPeerId)
                || !IsWireGuardKey(recovery.OriginalPeerPublicKey)
                || !IsWireGuardKey(recovery.ReplacementPublicKey)
                || recovery.ProfileId is null
                || recovery.ProfileId == Guid.Empty
                || string.IsNullOrWhiteSpace(recovery.TunnelName)))
        {
            throw new ArgumentException(
                "RouterOS key-replacement recovery metadata is incomplete.",
                nameof(recovery));
        }
    }

    private static bool IsWireGuardKey(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value)
                && Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
