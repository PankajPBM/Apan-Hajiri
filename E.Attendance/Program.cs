namespace E.Attendance;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;
    private const string OpenDashboardArgument = "--open-dashboard";
    private const string DbBootstrapArgument = "--db-bootstrap";
    private const string SetPortsArgument = "--set-ports";

    [STAThread]
    private static void Main()
    {
        var args = Environment.GetCommandLineArgs()
            .Skip(1)
            .ToArray();
        var openDashboardOnStartup = args
            .Any(arg => string.Equals(arg, OpenDashboardArgument, StringComparison.OrdinalIgnoreCase));
        var runDbBootstrap = args
            .Any(arg => string.Equals(arg, DbBootstrapArgument, StringComparison.OrdinalIgnoreCase));
        var runSetPorts = args
            .Any(arg => string.Equals(arg, SetPortsArgument, StringComparison.OrdinalIgnoreCase));

        if (!IsRunningAsAdministrator())
        {
            RelaunchAsAdministrator();
            return;
        }

        if (runDbBootstrap)
        {
            Environment.ExitCode = RunDatabaseBootstrap();
            return;
        }

        if (runSetPorts)
        {
            Environment.ExitCode = TryParseSetPortsArguments(args, out var apiPort, out var webPort)
                ? RunSetPorts(apiPort, webPort)
                : 1;
            return;
        }

        _singleInstanceMutex = new Mutex(true, "E_ATTENDANCE_SERVICE_CONTROLLER_SINGLE_INSTANCE", out var createdNew);
        if (!createdNew)
        {
            TryOpenDashboardShortcut();
            return;
        }

        ApplicationConfiguration.Initialize();
        try
        {
            try
            {
                Application.Run(new ServiceControllerContext(openDashboardOnStartup));
            }
            catch (System.IO.FileNotFoundException ex) when (ex.Message.Contains("System.ServiceProcess"))
            {
                MessageBox.Show(
                    "Service Controller requires .NET Desktop Runtime components.\n\n" +
                    "Please install .NET Desktop Runtime 10.0 or later from:\n" +
                    "https://dotnet.microsoft.com/download/dotnet/10.0\n\n" +
                    "Error: " + ex.Message,
                    "E_Att_Service - Missing Runtime Component",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to initialize E-Attendance Service Controller.\n\n" +
                    "Error: " + ex.Message,
                    "E_Att_Service - Startup Error",
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
        const int maxAttempts = 4;
        var delay = TimeSpan.FromSeconds(6);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var manager = new WebServiceManager();
                manager.EnsureDefaults();
                _ = manager.EnsurePostgreSqlReadyWithReport();
                return 0;
            }
            catch
            {
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
            return 0;
        }
        catch
        {
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
