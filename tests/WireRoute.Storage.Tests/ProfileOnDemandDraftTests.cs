using Microsoft.VisualStudio.TestTools.UnitTesting;
using WireRoute.App.Models;

namespace WireRoute.Storage.Tests;

[TestClass]
public sealed class ProfileOnDemandDraftTests
{
    [TestMethod]
    [DataRow(false, false, "Off")]
    [DataRow(true, false, "Ethernet")]
    [DataRow(false, true, "Wi-Fi")]
    [DataRow(true, true, "Ethernet, Wi-Fi")]
    public void LegacySummaryUsesSavedNetworks(bool ethernet, bool wifi, string expected) =>
        Assert.AreEqual(expected, new ProfileOnDemandDraft(ethernet, wifi).Summary(false, false));

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void AutomaticSummaryReplacesSingleProfileSummary(bool ethernet, bool wifi) =>
        Assert.AreEqual("Automatic profile switching", new ProfileOnDemandDraft(ethernet, wifi).Summary(true, true));

    [TestMethod]
    public void UnchangedSavedRulesRemainPaused()
    {
        var draft = new ProfileOnDemandDraft(true, true);
        Assert.AreEqual("Ethernet, Wi-Fi (paused)", draft.Summary(false, true));
        Assert.IsFalse(draft.ResumeSavedRules);
        Assert.AreEqual("Off", new ProfileOnDemandDraft(false, false).Summary(false, true));
    }

    [TestMethod]
    public void ResumeIsExplicitAndDoesNotMutateTheOriginalDraft()
    {
        var original = new ProfileOnDemandDraft(true, false);
        var edited = original with { WiFi = true, ResumeSavedRules = true };
        Assert.AreEqual("Ethernet, Wi-Fi", edited.Summary(false, true));
        Assert.AreEqual("Ethernet (paused)", original.Summary(false, true));
        Assert.IsFalse(original.WiFi);
        Assert.IsFalse(original.ResumeSavedRules);
        Assert.AreEqual("Automatic profile switching", edited.Summary(true, true));
    }
}
