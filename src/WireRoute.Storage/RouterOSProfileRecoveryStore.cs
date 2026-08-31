using System.Runtime.Versioning;

namespace WireRoute.Storage;

public enum RouterOSProfileRecoveryReason
{
    PendingRouterWrite,
    RouterWriteUncertain,
    ManagerImportFailed,
}

public sealed record RouterOSProfileRecovery(
    Guid Id,
    string DisplayName,
    string WgQuickConfiguration,
    DateTimeOffset CreatedAt,
    RouterOSProfileRecoveryReason Reason);

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
    }
}
