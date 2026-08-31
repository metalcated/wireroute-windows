using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

internal sealed class TunnelServiceController
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

        var operationDirectory = Path.Combine(runtimeRoot, Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(operationDirectory, profile.Name + ".conf");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                profile.Configuration,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await RunElevatedAsync(
                "/installephemeraltunnelservice",
                configurationPath,
                cancellationToken);
        }
        finally
        {
            TryDelete(configurationPath);
            TryDeleteDirectory(operationDirectory);
        }
    }

    public Task StopAsync(string profileName, CancellationToken cancellationToken = default) =>
        RunElevatedAsync("/stopephemeraltunnelservice", profileName, cancellationToken);

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
}
