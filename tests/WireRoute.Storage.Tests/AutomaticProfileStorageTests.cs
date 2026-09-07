using System.Text;
using System.Text.Json;
using WireRoute.Core.Profiles;
using WireRoute.Storage;

namespace WireRoute.Storage.Tests;

[TestClass]
public sealed class AutomaticProfileStorageTests
{
    [TestMethod]
    public void ExistingSettingsKeepLegacyBehaviorAndDoNotEnableAutomation()
    {
        var settings = JsonSerializer.Deserialize<WireRouteAppSettings>("""
            {"Theme":"System","TrayIconStyle":"Default","PreferredEndpoint":"",
            "DnsServers":"","SplitTunnelRoutes":"","PersistentKeepalive":25}
            """);
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.AutomaticProfiles.Enabled);
        Assert.IsFalse(settings.SingleProfileOnDemandSuspended);
        Assert.AreEqual(ProfileTarget.Default, settings.AutomaticProfiles.WiFi);
        Assert.IsNull(settings.AutomaticProfiles.DefaultProfileId);
    }

    [TestMethod]
    public async Task RulesAndLegacySuspensionRoundTripProtectedAndSurviveOtherSettingsEdits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WireRoute.AutomaticProfiles.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WireRouteSettingsStore(directory);
            var id = Guid.NewGuid();
            var settings = WireRouteAppSettings.Defaults with
            {
                AutomaticProfiles = new()
                {
                    Enabled = true, DefaultProfileId = id,
                    TrustedSsids = ["Private Test WiFi"], Assignments = [new("Office WiFi", id)],
                    Cellular = ProfileTarget.Off,
                },
                SingleProfileOnDemandSuspended = true,
            };
            await store.SaveAsync(settings);
            Assert.AreEqual(settings, await store.LoadAsync());
            var protectedText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(directory, "settings.dpapi")));
            Assert.IsFalse(protectedText.Contains("Private Test WiFi", StringComparison.Ordinal));
            var updated = (await store.LoadAsync()) with { Theme = "System", ActivityRetentionDays = 30 };
            await store.SaveAsync(updated);
            Assert.AreEqual(settings.AutomaticProfiles, (await store.LoadAsync()).AutomaticProfiles);
            var disabled = updated with { AutomaticProfiles = updated.AutomaticProfiles with { Enabled = false } };
            await store.SaveAsync(disabled);
            Assert.IsTrue((await store.LoadAsync()).SingleProfileOnDemandSuspended);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
