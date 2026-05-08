namespace E.Attendance;

internal sealed class ServiceControllerContext : ApplicationContext
{
    private readonly WebServiceManager _manager = new();
    private readonly bool _openDashboardOnStartup;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly System.Windows.Forms.Timer _timer;
    private ServiceControllerForm? _controllerForm;
    private bool _actionInProgress;
    private bool _isExiting;

    public ServiceControllerContext(bool openDashboardOnStartup = false)
    {
        _openDashboardOnStartup = openDashboardOnStartup;
        _manager.EnsureDefaults();

        _statusItem = new ToolStripMenuItem("Status: Checking...") { Enabled = false };
        _startItem = new ToolStripMenuItem("Start Service", null, async (_, _) => await RunActionAsync(() => Task.Run(StartService), "Service started."));
        _stopItem = new ToolStripMenuItem("Stop Service", null, async (_, _) => await RunActionAsync(() => Task.Run(StopService), "Service stopped."));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open E_Att_Service", null, (_, _) => OpenController()));
        menu.Items.Add(new ToolStripMenuItem("Open Dashboard", null, async (_, _) =>
            await RunActionAsync(() => Task.Run(() => _manager.OpenDashboard()), "Dashboard opened.")));
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        var trayIcon = LoadTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "E-Attendance Service",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => OpenController();

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) =>
        {
            if (!_actionInProgress && !_isExiting)
            {
                RefreshState();
            }
        };
        _timer.Start();

        if (_openDashboardOnStartup)
        {
            _ = RunActionAsync(async () =>
            {
                await Task.Run(StartService);
                await Task.Run(() => _manager.OpenDashboard());
            }, null, showSuccessBalloon: false);
        }
        else
        {
            _ = RunActionAsync(() => Task.Run(StartService), null, showSuccessBalloon: false);
        }

        RefreshState();
    }

    private void OpenController()
    {
        if (_controllerForm is null || _controllerForm.IsDisposed)
        {
            _controllerForm = new ServiceControllerForm(_manager);
            _controllerForm.FormClosed += (_, _) => _controllerForm = null;
        }

        _controllerForm.Show();
        _controllerForm.BringToFront();
        _controllerForm.RefreshState();
    }

    private void StartService()
    {
        _manager.StartService();
    }

    private void StopService()
    {
        _manager.StopService();
    }

    private void RefreshState()
    {
        var state = _manager.GetState();
        var configuredFrontendPort = state.FrontendPort;
        _statusItem.Text = $"Status: {(state.IsWebReachable ? "Running" : "Stopped")} (API {state.Port}, UI {configuredFrontendPort})";
        _startItem.Enabled = !_actionInProgress && !state.IsWebReachable;
        _stopItem.Enabled = !_actionInProgress && state.IsWebReachable;
        _trayIcon.Text = $"E_Att_Service {((state.IsWebReachable) ? "Running" : "Stopped")} API:{state.Port} UI:{configuredFrontendPort}";

        _controllerForm?.RefreshState();
    }

    private async Task RunActionAsync(Func<Task> action, string? successMessage, bool showSuccessBalloon = true)
    {
        if (_actionInProgress || _isExiting)
        {
            return;
        }

        _actionInProgress = true;
        _startItem.Enabled = false;
        _stopItem.Enabled = false;
        _statusItem.Text = "Status: Working...";

        try
        {
            await action();
            RefreshState();

            if (showSuccessBalloon && !string.IsNullOrWhiteSpace(successMessage))
            {
                _trayIcon.ShowBalloonTip(1200, "E_Att_Service", SanitizeMessage(successMessage), ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _trayIcon.ShowBalloonTip(3000, "E_Att_Service", SanitizeMessage(ex.Message), ToolTipIcon.Warning);
        }
        finally
        {
            _actionInProgress = false;
            if (!_isExiting)
            {
                RefreshState();
            }
        }
    }

    private static string SanitizeMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(password\s*=\s*)([^;,\s]+)", "$1***");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"(?i)(pwd\s*=\s*)([^;,\s]+)", "$1***");
        return sanitized;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    protected override void ExitThreadCore()
    {
        _isExiting = true;
        _timer.Stop();
        try
        {
            _manager.StopService();
        }
        catch
        {
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _timer.Dispose();
        base.ExitThreadCore();
    }
}
