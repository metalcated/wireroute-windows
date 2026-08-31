using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Win32.SafeHandles;
using WireRoute.Core.Manager;

namespace WireRoute.App;

public partial class App : Application
{
    private Window? window;
    private AppInstance? registeredInstance;
    private bool pendingActivation;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Length == 1)
        {
            var currentInstance = AppInstance.GetCurrent();
            registeredInstance = AppInstance.FindOrRegisterForKey("WireRoute.Main");
            if (!registeredInstance.IsCurrent)
            {
                try
                {
                    await registeredInstance.RedirectActivationToAsync(
                        currentInstance.GetActivatedEventArgs());
                }
                finally
                {
                    Exit();
                }
                return;
            }
            registeredInstance.Activated += RegisteredInstance_Activated;
        }

        var managerClient = TryOpenManagerClient(out var managerLaunchError);
        window = new MainWindow(managerClient, managerLaunchError);
        window.Activate();
        if (pendingActivation && window is MainWindow mainWindow)
        {
            pendingActivation = false;
            mainWindow.RestoreFromExternalActivation();
        }
    }

    private void RegisteredInstance_Activated(object? sender, AppActivationArguments args)
    {
        if (window is not MainWindow mainWindow)
        {
            pendingActivation = true;
            return;
        }
        _ = mainWindow.DispatcherQueue.TryEnqueue(mainWindow.RestoreFromExternalActivation);
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
            return new FileStream(handle, access, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
