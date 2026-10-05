namespace E.Attendance;

using E.Attendance.Core.Logging;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;
    private const string OpenDashboardArgument = "--open-dashboard";
    private const string DbBootstrapArgument = "--db-bootstrap";
    private const string SetPortsArgument = "--set-ports";
    private const string InstallServicesArgument = "--install-services";

    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += static (sender, e) =>
        {
            AppEventLogger.LogFatal("ServiceController", "Unhandled fatal exception occurred in Service Controller.", e.ExceptionObject as Exception);
        };
        Application.ThreadException += static (sender, e) =>
        {
            AppEventLogger.LogError("ServiceController", "Unhandled UI thread exception in Service Controller.", null, e.Exception);
        };

        var args = Environment.GetCommandLineArgs()
            .Skip(1)
            .ToArray();
        var openDashboardOnStartup = args
            .Any(arg => string.Equals(arg, OpenDashboardArgument, StringComparison.OrdinalIgnoreCase));
        var runDbBootstrap = args
            .Any(arg => string.Equals(arg, DbBootstrapArgument, StringComparison.OrdinalIgnoreCase));
        var runSetPorts = args
            .Any(arg => string.Equals(arg, SetPortsArgument, StringComparison.OrdinalIgnoreCase));
        var runInstallServices = args
            .Any(arg => string.Equals(arg, InstallServicesArgument, StringComparison.OrdinalIgnoreCase));

        if (!IsRunningAsAdministrator())
        {
            RelaunchAsAdministrator();
            return;
        }

        if (runDbBootstrap)
        {
            AppEventLogger.LogInfo("ServiceController", "Invoked with --db-bootstrap.");
            Environment.ExitCode = RunDatabaseBootstrap();
            return;
        }

        if (runSetPorts)
        {
            var parsed = TryParseSetPortsArguments(args, out var apiPort, out var webPort);
            AppEventLogger.LogInfo("ServiceController", $"Invoked with --set-ports (ApiPort={apiPort}, WebPort={webPort}, Success={parsed}).");
            Environment.ExitCode = parsed ? RunSetPorts(apiPort, webPort) : 1;
            return;
        }

        if (runInstallServices)
        {
            AppEventLogger.LogInfo("ServiceController", "Invoked with --install-services.");
            Environment.ExitCode = RunInstallServices();
            return;
        }

        _singleInstanceMutex = new Mutex(true, "E_ATTENDANCE_SERVICE_CONTROLLER_SINGLE_INSTANCE", out var createdNew);
        if (!createdNew)
        {
            AppEventLogger.LogInfo("ServiceController", "Another instance is already running; opening dashboard shortcut.");
            TryOpenDashboardShortcut();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Classic);
        try
        {
            try
            {
                AppEventLogger.LogInfo("ServiceController", $"Starting ServiceController UI (OpenDashboardOnStartup={openDashboardOnStartup}).");
                Application.Run(new ServiceControllerContext(openDashboardOnStartup));
            }
            catch (System.IO.FileNotFoundException ex) when (ex.Message.Contains("System.ServiceProcess"))
            {
                AppEventLogger.LogError("ServiceController", "Missing System.ServiceProcess runtime component.", null, ex);
                MessageBox.Show(
                    "Service Controller requires .NET Desktop Runtime components.\n\n" +
                    "Please install .NET Desktop Runtime 10.0 or later from:\n" +
                    "https://dotnet.microsoft.com/download/dotnet/10.0\n\n" +
                    "Error: " + ex.Message,
                    "Apan Hajiri - Missing Runtime Component",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                AppEventLogger.LogError("ServiceController", "Failed to initialize E-Attendance Service Controller.", null, ex);
                MessageBox.Show(
                    "Failed to initialize E-Attendance Service Controller.\n\n" +
                    "Error: " + ex.Message,
                    "Apan Hajiri - Startup Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    private static int RunDatabaseBootstrap()
    {
        const int maxAttempts = 2;
        var delay = TimeSpan.FromSeconds(3);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var manager = new WebServiceManager();
                manager.EnsureDefaults();
                var report = manager.EnsurePostgreSqlReadyWithReport();
                AppEventLogger.LogInfo("ServiceController", $"Database bootstrap attempt {attempt} completed successfully.");
                return 0;
            }
            catch (Exception ex)
            {
                AppEventLogger.LogError("ServiceController", $"Database bootstrap attempt {attempt}/{maxAttempts} failed.", null, ex);
                if (attempt >= maxAttempts)
                {
                    break;
                }

                Thread.Sleep(delay);
            }
        }

        return 1;
    }

    private static int RunSetPorts(int apiPort, int webPort)
    {
        try
        {
            var manager = new WebServiceManager();
            manager.EnsureDefaults();
            manager.SavePorts(apiPort, webPort);
            AppEventLogger.LogInfo("ServiceController", $"Ports saved successfully: API={apiPort}, Web={webPort}");
            return 0;
        }
        catch (Exception ex)
        {
            AppEventLogger.LogError("ServiceController", $"Failed to save ports (API={apiPort}, Web={webPort})", null, ex);
            return 1;
        }
    }

    // Installs the E-Attendance Windows service(s) headlessly during setup.
    // Invoked by the installer's [Run] step with --install-services so the service
    // is registered by default. Best-effort: never throws back to the installer.
    private static int RunInstallServices()
    {
        try
        {
            var manager = new WebServiceManager();
            manager.EnsureDefaults();
            var (ok, message) = manager.InstallWindowsServices();
            if (ok)
            {
                AppEventLogger.LogInfo("ServiceController", $"Windows services installed successfully: {message}");
            }
            else
            {
                AppEventLogger.LogWarning("ServiceController", $"Windows services install returned warning: {message}");
            }
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppEventLogger.LogError("ServiceController", "Exception while installing Windows services.", null, ex);
            return 1;
        }
    }

    private static bool TryParseSetPortsArguments(string[] args, out int apiPort, out int webPort)
    {
        apiPort = 0;
        webPort = 0;

        var index = Array.FindIndex(
            args,
            arg => string.Equals(arg, SetPortsArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length)
        {
            return false;
        }

        if (!int.TryParse(args[index + 1], out apiPort) ||
            !int.TryParse(args[index + 2], out webPort))
        {
            return false;
        }

        return apiPort is >= 1 and <= 65535 &&
               webPort is >= 1 and <= 65535 &&
               apiPort != webPort;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchAsAdministrator()
    {
        try
        {
            var executablePath = Application.ExecutablePath;
            var args = Environment.GetCommandLineArgs()
                .Skip(1)
                .Select(QuoteArgumentIfNeeded);
            var argumentLine = string.Join(" ", args);

            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = argumentLine,
                UseShellExecute = true,
                Verb = "runas"
            };

            _ = System.Diagnostics.Process.Start(info);
        }
        catch
        {
            MessageBox.Show(
                "Administrator permission is required to run E-Attendance Service.",
                "E_Att_Service",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void TryOpenDashboardShortcut()
    {
        try
        {
            var manager = new WebServiceManager();
            manager.EnsureDefaults();
            manager.OpenDashboard();
        }
        catch
        {
            // Keep shortcut invocation silent on failure.
        }
    }

    private static string QuoteArgumentIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Contains(' ') ? $"\"{value}\"" : value;
    }
}
