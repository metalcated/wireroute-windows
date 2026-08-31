using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WireRoute.App.Interop;

internal sealed record WireGuardTunnelMetrics(
    ulong ReceivedBytes,
    ulong SentBytes,
    DateTimeOffset? LastHandshake);

internal static class WireGuardRuntimeMetrics
{
    private const int ErrorMoreData = 234;
    private const int InterfaceSize = 80;
    private const int PeerSize = 136;
    private const int AllowedIpSize = 24;
    private const uint MaximumConfigurationSize = 16 * 1024 * 1024;

    public static bool TryRead(
        string adapterName,
        out WireGuardTunnelMetrics? metrics,
        out string? error)
    {
        metrics = null;
        error = null;
        nint adapter = 0;
        try
        {
            adapter = WireGuardOpenAdapter(adapterName);
            if (adapter == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            uint size = 1024;
            byte[] configuration;
            while (true)
            {
                if (size > MaximumConfigurationSize)
                {
                    throw new InvalidDataException("The WireGuardNT configuration is too large.");
                }
                configuration = new byte[size];
                if (WireGuardGetConfiguration(adapter, configuration, ref size))
                {
                    break;
                }
                if (Marshal.GetLastWin32Error() != ErrorMoreData)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            metrics = Parse(configuration, size);
            return true;
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException
                or InvalidDataException)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            if (adapter != 0)
            {
                WireGuardCloseAdapter(adapter);
            }
        }
    }

    private static unsafe WireGuardTunnelMetrics Parse(byte[] configuration, uint returnedSize)
    {
        var length = Math.Min(configuration.Length, checked((int)returnedSize));
        if (length < InterfaceSize)
        {
            throw new InvalidDataException("WireGuardNT returned an incomplete interface.");
        }

        ulong received = 0;
        ulong sent = 0;
        DateTimeOffset? lastHandshake = null;
        fixed (byte* start = configuration)
        {
            var interfaceConfiguration = (IoctlInterface*)start;
            var offset = InterfaceSize;
            for (var index = 0u; index < interfaceConfiguration->PeersCount; index++)
            {
                if (offset > length - PeerSize)
                {
                    throw new InvalidDataException("WireGuardNT returned an incomplete peer.");
                }
                var peer = (IoctlPeer*)(start + offset);
                received += peer->RxBytes;
                sent += peer->TxBytes;
                if (peer->LastHandshake != 0)
                {
                    var handshake = new DateTimeOffset(
                        DateTime.FromFileTimeUtc(checked((long)peer->LastHandshake)));
                    if (lastHandshake is null || handshake > lastHandshake)
                    {
                        lastHandshake = handshake;
                    }
                }

                var allowedBytes = checked((long)peer->AllowedIPsCount * AllowedIpSize);
                if (allowedBytes > int.MaxValue || offset + PeerSize + allowedBytes > length)
                {
                    throw new InvalidDataException("WireGuardNT returned incomplete allowed routes.");
                }
                offset += PeerSize + checked((int)allowedBytes);
            }
        }
        return new WireGuardTunnelMetrics(received, sent, lastHandshake);
    }

    [DllImport(
        "wireguard.dll",
        EntryPoint = "WireGuardOpenAdapter",
        CallingConvention = CallingConvention.StdCall,
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern nint WireGuardOpenAdapter(string name);

    [DllImport(
        "wireguard.dll",
        EntryPoint = "WireGuardCloseAdapter",
        CallingConvention = CallingConvention.StdCall)]
    private static extern void WireGuardCloseAdapter(nint adapter);

    [DllImport(
        "wireguard.dll",
        EntryPoint = "WireGuardGetConfiguration",
        CallingConvention = CallingConvention.StdCall,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WireGuardGetConfiguration(
        nint adapter,
        byte[] configuration,
        ref uint bytes);

    [StructLayout(LayoutKind.Sequential, Pack = 8, Size = InterfaceSize)]
    private unsafe struct IoctlInterface
    {
        public uint Flags;
        public ushort ListenPort;
        public fixed byte PrivateKey[32];
        public fixed byte PublicKey[32];
        public uint PeersCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8, Size = PeerSize)]
    private unsafe struct IoctlPeer
    {
        public uint Flags;
        public uint Reserved;
        public fixed byte PublicKey[32];
        public fixed byte PresharedKey[32];
        public ushort PersistentKeepalive;
        public SockAddrInet Endpoint;
        public ulong TxBytes;
        public ulong RxBytes;
        public ulong LastHandshake;
        public uint AllowedIPsCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct InAddr
    {
        public fixed byte Bytes[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct In6Addr
    {
        public fixed byte Bytes[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrIn
    {
        public ushort Family;
        public ushort Port;
        public InAddr Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrIn6
    {
        public ushort Family;
        public ushort Port;
        public uint FlowInfo;
        public In6Addr Address;
        public uint ScopeId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct SockAddrInet
    {
        [FieldOffset(0)]
        public SockAddrIn Ipv4;
        [FieldOffset(0)]
        public SockAddrIn6 Ipv6;
        [FieldOffset(0)]
        public ushort Family;
    }
}
