using System.Text.Json;
using WireRoute.Core.Telemetry;

namespace WireRoute.Core.Tests;

[TestClass]
public sealed class WireRouteRuntimeMetricsSnapshotTests
{
    [TestMethod]
    public void DeserializesNativeTunnelPayload()
    {
        const string payload = """
            {"version":1,"receivedBytes":1048576,"sentBytes":524288,"lastHandshakeFileTime":133853616000000000}
            """;

        var snapshot = JsonSerializer.Deserialize<WireRouteRuntimeMetricsSnapshot>(payload);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(1, snapshot.Version);
        Assert.AreEqual(1048576UL, snapshot.ReceivedBytes);
        Assert.AreEqual(524288UL, snapshot.SentBytes);
        Assert.AreEqual(133853616000000000UL, snapshot.LastHandshakeFileTime);
    }
}
