namespace WireRoute.Core.Profiles;

public readonly record struct ProfileSwitchStamp(long Revision, long Generation);

/// <summary>Session-only ownership deliberately does not survive an app restart.</summary>
public sealed class AutomaticProfileSession
{
    public Guid? OwnedProfileId { get; private set; }
    public string NetworkIdentity { get; private set; } = "none";
    public string? PausedNetwork { get; private set; }
    public long Revision { get; private set; }
    public long Generation { get; private set; }
    public bool IsPaused => PausedNetwork == NetworkIdentity;
    public ProfileSwitchStamp Stamp => new(Revision, Generation);

    public void ObserveNetwork(string identity)
    {
        if (NetworkIdentity == identity) return;
        NetworkIdentity = identity;
        Generation++;
        PausedNetwork = null;
    }

    public void SettingsChanged(bool enabled)
    {
        Revision++;
        PausedNetwork = null;
        if (!enabled) OwnedProfileId = null;
    }

    public void ManualControl()
    {
        Revision++;
        OwnedProfileId = null;
        PausedNetwork = NetworkIdentity;
    }

    public void PauseAfterFailure() => PausedNetwork = NetworkIdentity;
    public void Disconnected() => OwnedProfileId = null;

    public void Connected(Guid id, long revision, bool enabled)
    {
        // A network handover may finish late, but a manual action or policy edit
        // must never let that completion reclaim a manually controlled VPN.
        if (enabled && revision == Revision) OwnedProfileId = id;
    }

    public bool Permits(ProfileSwitchStamp stamp, bool enabled, IReadOnlyCollection<Guid> active,
        bool anotherVpn, Guid target, bool connecting) => enabled && stamp == Stamp && !IsPaused
        && !anotherVpn && (connecting ? active.Count == 0
            : OwnedProfileId == target && active.Count == 1 && active.Contains(target));
}
