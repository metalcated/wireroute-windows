using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    private const long MaximumRuntimeLogBytes = 2 * 1024 * 1024;
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
        var operationDirectory = RuntimeDirectory(profile);
        var configurationPath = Path.Combine(operationDirectory, profile.Name + ".conf");
        var metricsPath = Path.Combine(operationDirectory, "tunnel.metrics");
        var logPath = Path.Combine(operationDirectory, "tunnel.log");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                runtimeConfiguration,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            using (new FileStream(
                logPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            {
            }
            await File.WriteAllTextAsync(
                metricsPath,
                "{\"version\":0}",
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
            TryDelete(metricsPath);
            TryDeleteDirectory(operationDirectory);
            throw;
        }
        finally
        {
            TryDelete(configurationPath);
        }
    }

    public async Task StopAsync(
        WireRouteStoredProfile profile,
        CancellationToken cancellationToken = default)
    {
        await RunElevatedAsync(
            "/stopephemeraltunnelservice",
            profile.Name,
            cancellationToken);
        await dnsProxy.StopAsync(profile.Name);
        var operationDirectory = RuntimeDirectory(profile);
        TryDelete(Path.Combine(operationDirectory, "tunnel.metrics"));
        TryDeleteDirectory(operationDirectory);
    }

    public string ReadRuntimeLog(WireRouteStoredProfile profile)
    {
        var logPath = Path.Combine(RuntimeDirectory(profile), "tunnel.log");
        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var wasTrimmed = stream.Length > MaximumRuntimeLogBytes;
            if (wasTrimmed)
            {
                stream.Seek(-MaximumRuntimeLogBytes, SeekOrigin.End);
            }
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            var content = reader.ReadToEnd();
            if (wasTrimmed)
            {
                var firstNewLine = content.IndexOf('\n');
                if (firstNewLine >= 0)
                {
                    content = content[(firstNewLine + 1)..];
                }
            }
            return content.TrimEnd();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public bool TryReadMetrics(
        WireRouteStoredProfile profile,
        out WireGuardTunnelMetrics? metrics,
        out string? error)
    {
        metrics = null;
        error = null;
        var metricsPath = Path.Combine(RuntimeDirectory(profile), "tunnel.metrics");
        try
        {
            using var stream = new FileStream(
                metricsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > 4096)
            {
                throw new InvalidDataException("The runtime metrics file has an invalid size.");
            }
            var snapshot = JsonSerializer.Deserialize<TunnelRuntimeMetricsPayload>(stream);
            if (snapshot?.Version != 1)
            {
                throw new InvalidDataException("The tunnel is still publishing its first sample.");
            }
            DateTimeOffset? lastHandshake = null;
            if (snapshot.LastHandshakeFileTime > 0
                && snapshot.LastHandshakeFileTime <= long.MaxValue)
            {
                lastHandshake = new DateTimeOffset(
                    DateTime.FromFileTimeUtc((long)snapshot.LastHandshakeFileTime));
            }
            metrics = new WireGuardTunnelMetrics(
                snapshot.ReceivedBytes,
                snapshot.SentBytes,
                lastHandshake);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException
                or ArgumentOutOfRangeException)
        {
            error = exception.Message;
        }

        if (WireGuardRuntimeMetrics.TryRead(profile.Name, out metrics, out var adapterError))
        {
            error = null;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(adapterError))
        {
            error = error is null ? adapterError : error + " " + adapterError;
        }
        return false;
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

    private string RuntimeDirectory(WireRouteStoredProfile profile) =>
        Path.Combine(runtimeRoot, profile.Id.ToString("N"));

    private sealed record TunnelRuntimeMetricsPayload(
        int Version,
        ulong ReceivedBytes,
        ulong SentBytes,
        ulong LastHandshakeFileTime);

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
