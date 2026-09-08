using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using WireRoute.App.Interop;
using WireRoute.Core.Profiles;

namespace WireRoute.Storage.Tests;

[TestClass]
public sealed class ProfileNetworkMonitorTests
{
    [TestMethod]
    public void NativeRowMatchesWindowsSdkLayout()
    {
        var row = typeof(PhysicalNetworkInterface).GetNestedType("InterfaceRow", BindingFlags.NonPublic)!;
        Assert.AreEqual(1352, Marshal.SizeOf(row));
        Assert.AreEqual(new IntPtr(1152), Marshal.OffsetOf(row, "Flags"));
        Assert.AreEqual(new IntPtr(1168), Marshal.OffsetOf(row, "NetworkGuid"));
    }

    [TestMethod]
    public void UnknownAndLoopbackAdaptersAreNeverPhysical()
    {
        Assert.IsFalse(PhysicalNetworkInterface.IsHardware(Guid.NewGuid()));
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces().Where(i => i.NetworkInterfaceType == NetworkInterfaceType.Loopback))
            if (Guid.TryParse(item.Id, out var id)) Assert.IsFalse(PhysicalNetworkInterface.IsHardware(id));
    }

    [TestMethod]
    public void WindowsSnapshotReadsWithoutChangingNetworkOrRequestingLocation()
    {
        var snapshot = ProfileNetworkMonitor.Read(new HashSet<string>(), readWiFiNames: false);
        Assert.IsTrue(Enum.IsDefined(snapshot.Transport));
        Assert.IsFalse(string.IsNullOrEmpty(snapshot.Identity));
        Assert.IsNull(snapshot.Ssid);
    }
}
