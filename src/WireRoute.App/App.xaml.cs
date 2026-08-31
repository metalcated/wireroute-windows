using Microsoft.UI.Xaml;
using Microsoft.Win32.SafeHandles;
using WireRoute.Core.Manager;

namespace WireRoute.App;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var managerClient = TryOpenManagerClient(out var managerLaunchError);
        window = new MainWindow(managerClient, managerLaunchError);
        window.Activate();
    }

    private static ManagerProtocolClient? TryOpenManagerClient(out string? error)
    {
        error = null;
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 1)
        {
            return null;
        }

        if (arguments.Length != 5 || !arguments[1].Equals("/manager-v1", StringComparison.Ordinal))
        {
            error = "WireRoute was started with unsupported manager arguments.";
            return null;
        }

        FileStream? responseReader = null;
        FileStream? requestWriter = null;
        FileStream? eventReader = null;
        try
        {
            responseReader = OpenInheritedPipe(arguments[2], FileAccess.Read);
            requestWriter = OpenInheritedPipe(arguments[3], FileAccess.Write);
            eventReader = OpenInheritedPipe(arguments[4], FileAccess.Read);
            return new ManagerProtocolClient(responseReader, requestWriter, eventReader);
        }
        catch (Exception exception)
        {
            responseReader?.Dispose();
            requestWriter?.Dispose();
            eventReader?.Dispose();
            error = exception.Message;
            return null;
        }
    }

    private static FileStream OpenInheritedPipe(string argument, FileAccess access)
    {
        if (!ulong.TryParse(argument, out var value) || value == 0 || value > long.MaxValue)
        {
            throw new ArgumentException("An inherited manager pipe handle is invalid.");
        }

        var handle = new SafeFileHandle(new nint((long)value), ownsHandle: true);
        try
        {
            return new FileStream(handle, access, bufferSize: 4096, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
