using System.Text.Json;
using WireRoute.Core.Profiles;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class AutomaticProfileTests
{
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Work = Guid.NewGuid();
    private static readonly HashSet<Guid> Available = [Home, Work];
    private static AutomaticProfilePolicy Policy => new() { Enabled = true, DefaultProfileId = Home };

    [TestMethod]
    public void DisabledAndUnavailableNetworksHold()
    {
        Assert.IsNotNull(new AutomaticProfilePolicy().Decide(ProfileNetworkTransport.WiFi, "home", Available).HoldReason);
        Assert.IsNotNull(Policy.Decide(ProfileNetworkTransport.None, null, Available).HoldReason);
        Assert.IsNotNull(Policy.Decide(ProfileNetworkTransport.Other, null, Available).HoldReason);
    }

    [TestMethod]
    public void DefaultAndEachTransportHaveIndependentActions()
    {
        var policy = Policy with { WiFi = ProfileTarget.ForProfile(Work), Cellular = ProfileTarget.Off };
        Assert.AreEqual(Work, policy.Decide(ProfileNetworkTransport.WiFi, null, Available).ProfileId);
        Assert.AreEqual(new ProfileSwitchDecision(null), policy.Decide(ProfileNetworkTransport.Cellular, null, Available));
        Assert.AreEqual(Home, policy.Decide(ProfileNetworkTransport.Ethernet, null, Available).ProfileId);
        Assert.AreEqual(new ProfileSwitchDecision(null), (Policy with { DefaultProfileId = null })
            .Decide(ProfileNetworkTransport.Ethernet, null, Available));
    }

    [TestMethod]
    public void TrustedThenAssignedThenTransportPriorityIsExact()
    {
        var policy = Policy with
        {
            TrustedSsids = ["Home"],
            Assignments = [new("Office ", Work)],
        };
        policy.Validate();
        Assert.AreEqual(new ProfileSwitchDecision(null), policy.Decide(ProfileNetworkTransport.WiFi, "Home", Available));
        Assert.AreEqual(Work, policy.Decide(ProfileNetworkTransport.WiFi, "Office ", Available).ProfileId);
        Assert.AreEqual(Home, policy.Decide(ProfileNetworkTransport.WiFi, "Office", Available).ProfileId);
        Assert.AreEqual(Home, policy.Decide(ProfileNetworkTransport.WiFi, "home", Available).ProfileId);
        Assert.AreEqual(Home, policy.Decide(ProfileNetworkTransport.Ethernet, "Home", Available).ProfileId);
        // Even a corrupt overlapping record must never select a VPN on trusted Wi-Fi.
        var overlap = policy with { Assignments = [new("Home", Work)] };
        Assert.AreEqual(new ProfileSwitchDecision(null), overlap.Decide(ProfileNetworkTransport.WiFi, "Home", Available));
    }

    [TestMethod]
    public void UnknownSsidHoldsOnlyWhenNamesAreNeeded()
    {
        Assert.AreEqual(Home, Policy.Decide(ProfileNetworkTransport.WiFi, null, Available).ProfileId);
        var trusted = Policy with { TrustedSsids = ["Home"] };
        Assert.IsNotNull(trusted.Decide(ProfileNetworkTransport.WiFi, null, Available).HoldReason);
        Assert.IsNotNull(trusted.Decide(ProfileNetworkTransport.WiFi, "", Available).HoldReason);
        var assigned = Policy with { Assignments = [new("Office", Work)] };
        Assert.IsNotNull(assigned.Decide(ProfileNetworkTransport.WiFi, null, Available).HoldReason);
    }

    [TestMethod]
    public void MissingAssignedOrDefaultProfileHoldsInsteadOfDisconnectingOrFallingBack()
    {
        Assert.IsNotNull(Policy.Decide(ProfileNetworkTransport.Ethernet, null, new HashSet<Guid>()).HoldReason);
        var policy = Policy with { Assignments = [new("Office", Work)] };
        Assert.IsNotNull(policy.Decide(ProfileNetworkTransport.WiFi, "Office", new HashSet<Guid> { Home }).HoldReason);
        var direct = Policy with { Cellular = ProfileTarget.ForProfile(Work) };
        Assert.IsNotNull(direct.Decide(ProfileNetworkTransport.Cellular, null, new HashSet<Guid> { Home }).HoldReason);
    }

    [TestMethod]
    public void ValidateRejectsDuplicateOverlappingOrOversizedRules()
    {
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { TrustedSsids = ["Home", "Home"] }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { TrustedSsids = ["Home"], Assignments = [new("Home", Work)] }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { Assignments = [new("Office", Guid.Empty)] }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { TrustedSsids = [new string('é', 17)] }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { TrustedSsids = ["a\nb"] }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { TrustedSsids = Enumerable.Range(0, 65).Select(n => n.ToString()).ToArray() }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { WiFi = new(ProfileTargetKind.Profile) }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Policy with { Ethernet = new((ProfileTargetKind)99) }).Validate());
        (Policy with { TrustedSsids = [new string('é', 16), " Home ", "Home"] }).Validate();
    }

    [TestMethod]
    public void PolicyRoundTripsWithStableIdsAndValueEquality()
    {
        var policy = Policy with { Assignments = [new("Office", Work)], TrustedSsids = [" Home "] };
        var decoded = JsonSerializer.Deserialize<AutomaticProfilePolicy>(JsonSerializer.Serialize(policy));
        Assert.AreEqual(policy, decoded);
        Assert.AreEqual(policy.GetHashCode(), decoded!.GetHashCode());
        Assert.AreEqual(Work, decoded.Decide(ProfileNetworkTransport.WiFi, "Office", Available).ProfileId);
    }

    [TestMethod]
    public void ManualConnectionCannotBeClaimedOrStoppedEvenOnTrustedNetwork()
    {
        var session = new AutomaticProfileSession();
        Assert.IsFalse(session.Permits(session.Stamp, true, [Home], false, Home, connecting: false));
        Assert.IsFalse(session.Permits(session.Stamp, true, [Home], false, Work, connecting: true));
        Assert.IsFalse(session.Permits(session.Stamp, true, [], true, Work, connecting: true));
        Assert.IsTrue(session.Permits(session.Stamp, true, [], false, Work, connecting: true));
    }

    [TestMethod]
    public void HandoverStopsOnlyOwnedTunnelAndConnectsOnlyAfterItStops()
    {
        var session = new AutomaticProfileSession();
        session.Connected(Home, session.Revision, true);
        Assert.IsTrue(session.Permits(session.Stamp, true, [Home], false, Home, connecting: false));
        Assert.IsFalse(session.Permits(session.Stamp, true, [Home, Work], false, Home, connecting: false));
        Assert.IsFalse(session.Permits(session.Stamp, true, [Home], false, Work, connecting: true));
        session.Disconnected();
        Assert.IsTrue(session.Permits(session.Stamp, true, [], false, Work, connecting: true));
    }

    [TestMethod]
    public void ManualControlInvalidatesInFlightHandoverAndPausesCurrentNetwork()
    {
        var session = new AutomaticProfileSession();
        session.ObserveNetwork("WiFi:Home");
        var stamp = session.Stamp;
        session.Connected(Home, stamp.Revision, true);
        session.ManualControl();
        Assert.IsNull(session.OwnedProfileId);
        Assert.IsTrue(session.IsPaused);
        session.Connected(Work, stamp.Revision, true);
        Assert.IsNull(session.OwnedProfileId);
        Assert.IsFalse(session.Permits(stamp, true, [], false, Work, connecting: true));
        session.ObserveNetwork("WiFi:Home");
        Assert.IsTrue(session.IsPaused);
        session.ObserveNetwork("WiFi:Office");
        Assert.IsFalse(session.IsPaused);
        Assert.IsTrue(session.Permits(session.Stamp, true, [], false, Work, connecting: true));
        // Changing networks still cannot displace a manual VPN.
        Assert.IsFalse(session.Permits(session.Stamp, true, [Home], false, Work, connecting: true));
    }

    [TestMethod]
    public void NetworkChangeOrDisableRejectsStaleWork()
    {
        var session = new AutomaticProfileSession();
        var old = session.Stamp;
        session.ObserveNetwork("Ethernet");
        Assert.IsFalse(session.Permits(old, true, [], false, Work, connecting: true));
        session.Connected(Home, session.Revision, true);
        old = session.Stamp;
        session.SettingsChanged(false);
        Assert.IsNull(session.OwnedProfileId);
        Assert.IsFalse(session.Permits(old, false, [], false, Work, connecting: true));
        session.SettingsChanged(true);
        Assert.IsNull(session.OwnedProfileId);
    }

    [TestMethod]
    public void DeniedStopRetainsOwnershipButDoesNotRepeatedlyPrompt()
    {
        var session = new AutomaticProfileSession();
        session.Connected(Home, session.Revision, true);
        session.PauseAfterFailure();
        Assert.IsFalse(session.Permits(session.Stamp, true, [Home], false, Home, connecting: false));
        Assert.AreEqual(Home, session.OwnedProfileId);
        session.SettingsChanged(true);
        Assert.IsTrue(session.Permits(session.Stamp, true, [Home], false, Home, connecting: false));
    }
}
