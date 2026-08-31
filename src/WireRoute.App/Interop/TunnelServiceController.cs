using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using WireRoute.Core.Profiles;
using WireRoute.Storage;

namespace WireRoute.App.Interop;

internal enum LocalTunnelState
{
    Inactive,
    Activating,
    Active,
    Deactivating,
    Unavailable,
}

internal sealed class TunnelServiceController : IAsyncDisposable
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const int ErrorServiceDoesNotExist = 1060;

    private readonly string backendPath = Path.Combine(AppContext.BaseDirectory, "wireguard.exe");
    private readonly string runtimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WireRoute",
        "Runtime");
    private readonly DnsOverHttpsProxy dnsProxy = new();

    public bool IsAvailable => File.Exists(backendPath);

    public LocalTunnelState GetState(string profileName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return LocalTunnelState.Unavailable;
        }

        try
        {
            var service = OpenService(manager, ServiceName(profileName), ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? LocalTunnelState.Inactive
                    : LocalTunnelState.Unavailable;
            }

            try
            {
                var status = new ServiceStatusProcess();
                if (!QueryServiceStatusEx(
                    service,
                    ScStatusProcessInfo,
                    ref status,
                    Marshal.SizeOf<ServiceStatusProcess>(),
                    out _))
                {
                    return LocalTunnelState.Unavailable;
                }

                return status.CurrentState switch
                {
                    ServiceStartPending => LocalTunnelState.Activating,
                    ServiceRunning => LocalTunnelState.Active,
                    ServiceStopPending => LocalTunnelState.Deactivating,
                    ServiceStopped => LocalTunnelState.Inactive,
                    _ => LocalTunnelState.Unavailable,
                };
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    public async Task StartAsync(
        WireRouteStoredProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException(
                "The native WireRoute tunnel backend is not installed beside the app.",
                backendPath);
        }

        var runtimeConfiguration = await PrepareRuntimeConfigurationAsync(
            profile,
            cancellationToken);
        var operationDirectory = Path.Combine(runtimeRoot, Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(operationDirectory, profile.Name + ".conf");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                runtimeConfiguration,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await RunElevatedAsync(
                "/installephemeraltunnelservice",
                configurationPath,
                cancellationToken);
        }
        catch
        {
            if (profile.DnsProtectionMode == StoredDnsProtectionMode.Encrypted)
            {
                await dnsProxy.StopAsync(profile.Name);
            }
            throw;
        }
        finally
        {
            TryDelete(configurationPath);
            TryDeleteDirectory(operationDirectory);
        }
    }

    public async Task StopAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        await RunElevatedAsync(
            "/stopephemeraltunnelservice",
            profileName,
            cancellationToken);
        await dnsProxy.StopAsync(profileName);
    }

    public bool HasActiveEncryptedDns(string profileName) =>
        dnsProxy.IsRunningFor(profileName);

    public async Task RestoreEncryptedDnsAsync(
        WireRouteStoredProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (profile.DnsProtectionMode != StoredDnsProtectionMode.Encrypted
            || GetState(profile.Name) != LocalTunnelState.Active)
        {
            return;
        }
        _ = await PrepareRuntimeConfigurationAsync(profile, cancellationToken);
    }

    private async Task<string> PrepareRuntimeConfigurationAsync(
        WireRouteStoredProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.DnsProtectionMode != StoredDnsProtectionMode.Encrypted)
        {
            return profile.Configuration;
        }
        if (!Uri.TryCreate(profile.DnsResolverUrl, UriKind.Absolute, out var resolver)
            || resolver.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "This profile does not contain a valid HTTPS DNS resolver.");
        }

        var bootstrap = profile.DnsBootstrapAddresses
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null)
            .Cast<IPAddress>()
            .Distinct()
            .Take(8)
            .ToArray();
        if (bootstrap.Length == 0)
        {
            bootstrap = (await Dns.GetHostAddressesAsync(
                resolver.DnsSafeHost,
                cancellationToken))
                .Where(address =>
                    address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
                        or System.Net.Sockets.AddressFamily.InterNetworkV6)
                .Distinct()
                .Take(8)
                .ToArray();
        }
        if (bootstrap.Length == 0)
        {
            throw new InvalidOperationException(
                "WireRoute could not resolve an address for the encrypted DNS resolver.");
        }

        await dnsProxy.StartAsync(
            profile.Name,
            resolver,
            bootstrap,
            cancellationToken);
        var parsed = WireGuardConfigParser.Parse(profile.Configuration, profile.Name);
        return WireGuardConfigFormatter.ToWgQuick(
            parsed,
            dnsServers: new[] { IPAddress.Loopback.ToString() });
    }

    private async Task RunElevatedAsync(
        string command,
        string argument,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = backendPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the elevated tunnel command.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    command.Contains("stop", StringComparison.Ordinal)
                        ? "The tunnel could not be disconnected."
                        : "The tunnel could not be activated. Review the WireRoute log for details.");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The Windows elevation request was canceled.", exception);
        }
    }

    private static string ServiceName(string profileName) => "WireGuardTunnel$" + profileName;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch
        {
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(
        IntPtr serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        ref ServiceStatusProcess buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    public ValueTask DisposeAsync() => dnsProxy.DisposeAsync();
}
