using System.Text;

namespace WireRoute.Core.Profiles;

public enum ProfileNetworkTransport { None, Other, WiFi, Cellular, Ethernet }
public enum ProfileTargetKind { Off, Default, Profile }

public sealed record ProfileTarget(ProfileTargetKind Kind, Guid? ProfileId = null)
{
    public static ProfileTarget Off { get; } = new(ProfileTargetKind.Off);
    public static ProfileTarget Default { get; } = new(ProfileTargetKind.Default);
    public static ProfileTarget ForProfile(Guid id) => new(ProfileTargetKind.Profile, id);
}

public sealed record WiFiProfileAssignment(string Ssid, Guid ProfileId);
public sealed record ProfileSwitchDecision(Guid? ProfileId, string? HoldReason = null);

/// <summary>Network rules are independent of Windows and never contain tunnel secrets.</summary>
public sealed record AutomaticProfilePolicy
{
    public bool Enabled { get; init; }
    public Guid? DefaultProfileId { get; init; }
    public ProfileTarget WiFi { get; init; } = ProfileTarget.Default;
    public ProfileTarget Cellular { get; init; } = ProfileTarget.Default;
    public ProfileTarget Ethernet { get; init; } = ProfileTarget.Default;
    public IReadOnlyList<string> TrustedSsids { get; init; } = Array.Empty<string>();
    public IReadOnlyList<WiFiProfileAssignment> Assignments { get; init; } = Array.Empty<WiFiProfileAssignment>();
    public bool NeedsWiFiNames => TrustedSsids.Count != 0 || Assignments.Count != 0;

    public bool Equals(AutomaticProfilePolicy? other) => other is not null
        && Enabled == other.Enabled && DefaultProfileId == other.DefaultProfileId
        && WiFi == other.WiFi && Cellular == other.Cellular && Ethernet == other.Ethernet
        && TrustedSsids.SequenceEqual(other.TrustedSsids, StringComparer.Ordinal)
        && Assignments.SequenceEqual(other.Assignments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled); hash.Add(DefaultProfileId); hash.Add(WiFi); hash.Add(Cellular); hash.Add(Ethernet);
        foreach (var name in TrustedSsids) hash.Add(name, StringComparer.Ordinal);
        foreach (var assignment in Assignments) hash.Add(assignment);
        return hash.ToHashCode();
    }

    public void Validate()
    {
        if (TrustedSsids is null || Assignments is null || Assignments.Any(a => a is null))
            throw new ArgumentException("Wi-Fi rules are invalid.");
        var names = TrustedSsids.Concat(Assignments.Select(a => a.Ssid)).ToArray();
        if (names.Length > 64 || names.Any(n => string.IsNullOrEmpty(n)
            || n.Contains('\r') || n.Contains('\n') || Encoding.UTF8.GetByteCount(n) > 32))
            throw new ArgumentException("Enter up to 64 Wi-Fi names, each on one line and no longer than 32 UTF-8 bytes.");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Each exact Wi-Fi name must appear once, either as trusted or assigned to a profile.");
        if (DefaultProfileId == Guid.Empty || Assignments.Any(a => a.ProfileId == Guid.Empty)
            || new[] { WiFi, Cellular, Ethernet }.Any(t => t is null || !Enum.IsDefined(t.Kind)
                || (t.Kind == ProfileTargetKind.Profile && (t.ProfileId is null || t.ProfileId == Guid.Empty))))
            throw new ArgumentException("Choose a saved profile for every profile action.");
    }

    public ProfileSwitchDecision Decide(ProfileNetworkTransport transport, string? ssid, IReadOnlySet<Guid> available)
    {
        if (!Enabled) return new(null, "Automatic profiles are off.");
        if (transport == ProfileNetworkTransport.None) return new(null, "Waiting for a network.");
        if (transport == ProfileNetworkTransport.Other) return new(null, "This network type has no automatic rule.");
        ProfileTarget target;
        switch (transport)
        {
            case ProfileNetworkTransport.Ethernet: target = Ethernet; break;
            case ProfileNetworkTransport.Cellular: target = Cellular; break;
            case ProfileNetworkTransport.WiFi:
                if (NeedsWiFiNames && string.IsNullOrEmpty(ssid))
                    return new(null, "Wi-Fi name unavailable. Review Wi-Fi name access; the connection is unchanged.");
                if (TrustedSsids.Contains(ssid, StringComparer.Ordinal)) return new(null);
                var assignment = Assignments.FirstOrDefault(a => a.Ssid.Equals(ssid, StringComparison.Ordinal));
                target = assignment is null ? WiFi : ProfileTarget.ForProfile(assignment.ProfileId);
                break;
            default: return new(null, "This network type has no automatic rule.");
        }
        var id = target.Kind switch
        {
            ProfileTargetKind.Off => null,
            ProfileTargetKind.Default => DefaultProfileId,
            _ => target.ProfileId,
        };
        return id is null || available.Contains(id.Value) ? new(id)
            : new(null, "Assigned profile unavailable. Review Automatic profiles; the connection is unchanged.");
    }
}
