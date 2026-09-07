using System.Runtime.InteropServices;

namespace WireRoute.App.Interop;

internal static class PhysicalNetworkInterface
{
    public static unsafe bool IsHardware(Guid adapterId)
    {
        if (ConvertInterfaceGuidToLuid(ref adapterId, out var luid) != 0) return false;
        var row = new InterfaceRow { InterfaceLuid = luid };
        return GetIfEntry2(ref row) == 0 && (row.Flags & 1) != 0 && (row.Flags & 2) == 0;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref InterfaceRow row);

    // MIB_IF_ROW2 from netioapi.h; native fixed arrays and alignment work on
    // both x64 and ARM64. HardwareInterface avoids mistaking VPNs for Ethernet.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct InterfaceRow
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public fixed ulong Counters[18];
    }
}
