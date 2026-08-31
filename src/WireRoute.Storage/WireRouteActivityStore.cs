using System.Runtime.Versioning;

namespace WireRoute.Storage;

public enum WireRouteActivityKind
{
    AppStarted,
    ProfileImported,
    ProfileCreated,
    ProfileUpdated,
    ProfileDeleted,
    ProfileActivated,
    ProfileDeactivated,
    OnDemandMatched,
    TunnelError,
    RouterOSProfileCreated,
    OnDemandUnmatched,
}

public sealed record WireRouteActivityEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    WireRouteActivityKind Kind,
    Guid? ProfileId,
    string? ProfileName,
    string Message);

public sealed record WireRouteConnectionSession(
    Guid Id,
    Guid ProfileId,
    string ProfileName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset LastSampleAt,
    ulong ReceivedBytes,
    ulong SentBytes,
    DateTimeOffset? LastHandshake);

[SupportedOSPlatform("windows")]
public sealed class WireRouteActivityStore
{
    private const int MaximumEntries = 1000;
    private readonly ProtectedJsonFile<List<WireRouteActivityEntry>> entries;
    private readonly ProtectedJsonFile<List<WireRouteConnectionSession>> sessions;

    public WireRouteActivityStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? WireRouteStoragePaths.DefaultDirectory;
        entries = new ProtectedJsonFile<List<WireRouteActivityEntry>>(
            Path.Combine(directory, "activity.dpapi"),
            () => []);
        sessions = new ProtectedJsonFile<List<WireRouteConnectionSession>>(
            Path.Combine(directory, "activity-sessions.dpapi"),
            () => []);
    }

    public async Task<IReadOnlyList<WireRouteActivityEntry>> LoadAsync(
        Guid? profileId = null,
        CancellationToken cancellationToken = default)
    {
        var stored = await entries.ReadAsync(cancellationToken).ConfigureAwait(false);
        return stored
            .Where(entry => profileId is null || entry.ProfileId == profileId)
            .OrderByDescending(entry => entry.Timestamp)
            .ToArray();
    }

    public async Task AppendAsync(
        WireRouteActivityEntry entry,
        CancellationToken cancellationToken = default)
    {
        Validate(entry);
        _ = await entries.UpdateAsync(current =>
        {
            var updated = current.ToList();
            updated.Add(entry);
            return updated
                .OrderByDescending(value => value.Timestamp)
                .Take(MaximumEntries)
                .OrderBy(value => value.Timestamp)
                .ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> BeginConnectionSessionAsync(
        Guid profileId,
        string profileName,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile identifier is required.", nameof(profileId));
        }
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("A profile name is required.", nameof(profileName));
        }
        if (startedAt == default)
        {
            throw new ArgumentException("A session start time is required.", nameof(startedAt));
        }

        var id = Guid.NewGuid();
        _ = await sessions.UpdateAsync(current =>
        {
            var updated = current.Select(session =>
                session.ProfileId == profileId && session.EndedAt is null
                    ? session with { EndedAt = session.LastSampleAt }
                    : session).ToList();
            updated.Add(new WireRouteConnectionSession(
                id,
                profileId,
                profileName.Trim(),
                startedAt,
                null,
                startedAt,
                0,
                0,
                null));
            return TrimSessions(updated);
        }, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task UpdateConnectionSessionAsync(
        Guid sessionId,
        Guid profileId,
        DateTimeOffset sampledAt,
        ulong receivedBytes,
        ulong sentBytes,
        DateTimeOffset? lastHandshake,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || profileId == Guid.Empty || sampledAt == default)
        {
            throw new ArgumentException("A valid session, profile, and sample time are required.");
        }
        _ = await sessions.UpdateAsync(current => current.Select(session =>
            session.Id == sessionId && session.ProfileId == profileId
                ? session with
                {
                    LastSampleAt = sampledAt,
                    ReceivedBytes = receivedBytes,
                    SentBytes = sentBytes,
                    LastHandshake = lastHandshake,
                }
                : session).ToList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task EndConnectionSessionAsync(
        Guid sessionId,
        DateTimeOffset endedAt,
        ulong receivedBytes,
        ulong sentBytes,
        DateTimeOffset? lastHandshake,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || endedAt == default)
        {
            throw new ArgumentException("A valid session and end time are required.");
        }
        _ = await sessions.UpdateAsync(current => current.Select(session =>
            session.Id == sessionId
                ? session with
                {
                    EndedAt = endedAt,
                    LastSampleAt = endedAt > session.LastSampleAt ? endedAt : session.LastSampleAt,
                    ReceivedBytes = receivedBytes,
                    SentBytes = sentBytes,
                    LastHandshake = lastHandshake,
                }
                : session).ToList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WireRouteConnectionSession>> LoadConnectionSessionsAsync(
        Guid profileId,
        int limit = 24,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile identifier is required.", nameof(profileId));
        }
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        var stored = await sessions.ReadAsync(cancellationToken).ConfigureAwait(false);
        return stored
            .Where(session => session.ProfileId == profileId)
            .OrderByDescending(session => session.StartedAt)
            .Take(limit)
            .ToArray();
    }

    public async Task ClearCompletedConnectionSessionsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        _ = await sessions.UpdateAsync(current => current
            .Where(session => session.ProfileId != profileId || session.EndedAt is null)
            .ToList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeConnectionSessionsAsync(
        DateTimeOffset completedBefore,
        CancellationToken cancellationToken = default)
    {
        _ = await sessions.UpdateAsync(current => current
            .Where(session => session.EndedAt is null || session.EndedAt >= completedBefore)
            .ToList(), cancellationToken).ConfigureAwait(false);
    }

    private static List<WireRouteConnectionSession> TrimSessions(
        IEnumerable<WireRouteConnectionSession> values) =>
        values
            .OrderByDescending(session => session.StartedAt)
            .Take(MaximumEntries)
            .OrderBy(session => session.StartedAt)
            .ToList();

    private static void Validate(WireRouteActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id == Guid.Empty)
        {
            throw new ArgumentException("An activity identifier is required.", nameof(entry));
        }

        if (entry.Timestamp == default)
        {
            throw new ArgumentException("An activity timestamp is required.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.Message))
        {
            throw new ArgumentException("An activity message is required.", nameof(entry));
        }
    }
}
