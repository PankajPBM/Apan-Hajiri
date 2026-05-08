using System.Net.Http;
using System.Text.RegularExpressions;

namespace E.Attendance;

internal sealed class ServiceControllerForm : Form
{
    private static readonly Color PageBackgroundColor = Color.FromArgb(240, 246, 252);
    private static readonly Color SurfaceColor = Color.White;
    private static readonly Color SurfaceMutedColor = Color.FromArgb(246, 250, 255);
    private static readonly Color BorderColor = Color.FromArgb(206, 222, 238);
    private static readonly Color TitleColor = Color.FromArgb(13, 51, 77);
    private static readonly Color MutedTextColor = Color.FromArgb(83, 115, 141);
    private static readonly Color PrimaryColor = Color.FromArgb(45, 110, 156);
    private static readonly Color PrimaryBorderColor = Color.FromArgb(34, 90, 128);
    private static readonly Color SuccessColor = Color.FromArgb(46, 130, 77);
    private static readonly Color SuccessBorderColor = Color.FromArgb(35, 105, 60);
    private static readonly Color DangerColor = Color.FromArgb(188, 66, 66);
    private static readonly Color DangerBorderColor = Color.FromArgb(150, 50, 50);

    private readonly WebServiceManager _manager;
    private readonly Label _serviceStatus = new() { AutoSize = true };
    private readonly Label _frontendStatus = new() { AutoSize = true };
    private readonly Label _dbEngine = new() { AutoSize = true };
    private readonly Label _dbStatus = new() { AutoSize = true };
    private readonly Label _dbMetaStatus = new() { AutoSize = true };
    private readonly Label _dbPreset = new() { AutoSize = true };
    private readonly Label _dbInstallHint = new() { AutoSize = true };
    private readonly FlowLayoutPanel _serverRelatedServicesPanel = new() { WrapContents = true, AutoSize = false };
    private readonly FlowLayoutPanel _dbRelatedServicesPanel = new() { WrapContents = true, AutoSize = false };
    private readonly NumericUpDown _apiPortInput = new() { Minimum = 1, Maximum = 65535, Width = 96 };
    private readonly NumericUpDown _webPortInput = new() { Minimum = 1, Maximum = 65535, Width = 96 };
    private readonly Button _toggleService = new() { Width = 150, Height = 34 };
    private readonly Button _restartService = new() { Width = 150, Height = 34, Text = "Restart Service" };
    private readonly Button _togglePostgreSql = new() { Width = 150, Height = 34, Text = "Start PostgreSQL" };
    private readonly Button _restartPostgreSql = new() { Width = 164, Height = 34, Text = "Restart PostgreSQL" };
    private readonly Button _savePortsButton = new() { Width = 110, Height = 34, Text = "Save Ports" };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2500 };

    private readonly List<Control> _busyControls = new();
    private bool _busy;
    private bool _refreshInProgress;
    private bool _isRunning;
    private bool _isPostgreSqlRunning;
    private bool _portInputsDirty;

    public ServiceControllerForm(WebServiceManager manager)
    {
        _manager = manager;
        Text = "E_Att_Service";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 500);
        Width = 880;
        Height = 560;
        BackColor = PageBackgroundColor;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        DoubleBuffered = true;

        BuildLayout();
        WireEvents();
        RefreshState();
        _refreshTimer.Interval = 5000;
        _refreshTimer.Start();
    }

    public void RefreshState() => _ = RefreshStateAsync();

    private async Task RefreshStateAsync()
    {
        if (_busy || _refreshInProgress || IsDisposed)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var state = _manager.GetState();
                var services = _manager.GetRelatedServices(state);
                var preset = _manager.GetPostgreSqlPresetSummary();
                var discovery = _manager.DiscoverPostgreSql();
                return (state, services, preset, discovery);
            });

            _isRunning = snapshot.state.IsWebReachable;
            _serviceStatus.Text = _isRunning
                ? $"Service is RUNNING (API {snapshot.state.Port})"
                : $"Service is STOPPED (API {snapshot.state.Port})";
            _serviceStatus.ForeColor = _isRunning ? SuccessBorderColor : DangerBorderColor;

            _frontendStatus.Text = $"Web UI: {(snapshot.state.IsFrontendReachable ? "Running" : "Stopped")} (Configured Port {snapshot.state.FrontendPort})";
            _frontendStatus.ForeColor = snapshot.state.IsFrontendReachable ? SuccessBorderColor : DangerBorderColor;

            _dbEngine.Text = $"Database Engine: {snapshot.state.DatabaseEngine}";
            _dbStatus.Text = $"DB Status: {snapshot.state.PostgresStatus}";
            _dbMetaStatus.Text = snapshot.state.PostgresStatus.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                ? "Using fallback port"
                : "Using configured port";
            _dbMetaStatus.ForeColor = snapshot.state.PostgresStatus.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(150, 96, 17)
                : SuccessBorderColor;
            var pgStatus = snapshot.state.PostgresStatus.ToLowerInvariant();
            _isPostgreSqlRunning = pgStatus.Contains("running") || pgStatus.Contains("connected");
            _dbPreset.Text = snapshot.preset;
            _dbInstallHint.Text = snapshot.discovery.message;
            _dbInstallHint.ForeColor = snapshot.discovery.installed ? SuccessBorderColor : Color.FromArgb(150, 96, 17);

            if (!_portInputsDirty && !_apiPortInput.Focused && !_webPortInput.Focused)
            {
                _apiPortInput.Value = snapshot.state.Port;
                _webPortInput.Value = snapshot.state.FrontendPort;
            }

            _toggleService.Text = _isRunning ? "Stop Service" : "Start Service";
            _togglePostgreSql.Text = _isPostgreSqlRunning ? "Stop PostgreSQL" : "Start PostgreSQL";
            
            // Port changing only allowed when service is stopped
            _savePortsButton.Enabled = !_isRunning && !snapshot.state.IsFrontendReachable;
            _apiPortInput.Enabled = _savePortsButton.Enabled;
            _webPortInput.Enabled = _savePortsButton.Enabled;

            ApplyRuntimeButtonStyles();
            RefreshRelatedServices(snapshot.services);
        }
        catch (Exception ex)
        {
            _serviceStatus.Text = $"Refresh failed: {SanitizeMessage(ex.Message)}";
            _serviceStatus.ForeColor = DangerBorderColor;
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14),
            BackColor = PageBackgroundColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var topCard = new Panel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(14),
            BackColor = SurfaceColor,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 12)
        };

        var heading = new Label
        {
            Text = "E-Attendance Service",
            AutoSize = true,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = TitleColor,
            Margin = new Padding(0, 0, 0, 4)
        };
        var subtitle = new Label
        {
            Text = "Manage service and database status",
            AutoSize = true,
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 0, 0, 12)
        };

        StyleStatusPill(_serviceStatus);
        StyleStatusPill(_frontendStatus);
        StyleStatusPill(_dbEngine);
        StyleStatusPill(_dbStatus);
        StyleStatusPill(_dbMetaStatus);

        var top = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = SurfaceColor
        };
        top.Controls.Add(heading);
        top.Controls.Add(subtitle);
        top.Controls.Add(_serviceStatus);
        top.Controls.Add(_frontendStatus);
        top.Controls.Add(_dbEngine);
        top.Controls.Add(_dbStatus);
        top.Controls.Add(_dbMetaStatus);
        topCard.Controls.Add(top);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Padding = new Point(14, 6)
        };
        var serverTab = new TabPage("Service Control");
        var dbTab = new TabPage("DB Management");
        serverTab.BackColor = PageBackgroundColor;
        dbTab.BackColor = PageBackgroundColor;

        var serverPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
            BackColor = PageBackgroundColor
        };
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serverPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        serverPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        serverPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var serviceActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = PageBackgroundColor
        };
        AddAction(serviceActions, "Open Dashboard", () => RunSafe(() => _manager.OpenDashboard()));
        AddAction(serviceActions, "Refresh", RefreshState);
        serviceActions.Controls.Add(_toggleService);
        serviceActions.Controls.Add(_restartService);
        StylePrimaryButton(_restartService, width: 154);
        StyleRuntimeButton(_toggleService, _isRunning);

        var portConfig = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8),
            BackColor = PageBackgroundColor
        };
        portConfig.Controls.Add(new Label { Text = "API Port:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        portConfig.Controls.Add(_apiPortInput);
        portConfig.Controls.Add(new Label { Text = "Web UI Port:", AutoSize = true, Margin = new Padding(12, 8, 4, 0) });
        portConfig.Controls.Add(_webPortInput);
        portConfig.Controls.Add(_savePortsButton);
        StyleSecondaryButton(_savePortsButton, width: 110);

        _serverRelatedServicesPanel.Dock = DockStyle.Fill;
        _serverRelatedServicesPanel.AutoScroll = true;
        _serverRelatedServicesPanel.BackColor = SurfaceColor;
        _serverRelatedServicesPanel.BorderStyle = BorderStyle.FixedSingle;
        _serverRelatedServicesPanel.Padding = new Padding(8);
        _serverRelatedServicesPanel.Margin = new Padding(0);
        serverPanel.Controls.Add(serviceActions, 0, 0);
        serverPanel.Controls.Add(BuildGroupBox("Port Configuration", portConfig), 0, 1);
        serverPanel.Controls.Add(new Label { Text = "Related Services", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = TitleColor, Margin = new Padding(0, 8, 0, 8) }, 0, 2);
        serverPanel.Controls.Add(_serverRelatedServicesPanel, 0, 3);
        serverTab.Controls.Add(serverPanel);

        var dbActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, BackColor = PageBackgroundColor, Margin = new Padding(0, 0, 0, 10) };
        AddAction(dbActions, "Refresh DB", RefreshState);
        
        var pgButton = new Button { Text = "Toggle PostgreSQL", Width = 140, Height = 36, Margin = new Padding(0, 0, 8, 8) };
        StyleSecondaryButton(pgButton);
        pgButton.Click += async (_, _) => await RunBusyAction(() => Task.Run(() =>
        {
            if (_isPostgreSqlRunning) _manager.StopPostgreSqlService();
            else _manager.StartPostgreSqlService();
        }), "PostgreSQL toggled.");
        dbActions.Controls.Add(pgButton);
        
        var testButton = new Button { Text = "Test DB", Width = 90, Height = 36, Margin = new Padding(0, 0, 8, 8) };
        StyleSecondaryButton(testButton);
        testButton.Click += (_, _) =>
        {
            var result = _manager.TestPostgreSqlConnection();
            MessageBox.Show(SanitizeMessage(result.message), "E-Attendance - DB Test", MessageBoxButtons.OK, result.ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshState();
        };
        dbActions.Controls.Add(testButton);

        var dbPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10),
            BackColor = PageBackgroundColor
        };
        dbPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dbPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dbPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dbPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dbPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dbPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _dbRelatedServicesPanel.Dock = DockStyle.Fill;
        _dbRelatedServicesPanel.AutoScroll = true;
        _dbRelatedServicesPanel.BackColor = SurfaceColor;
        _dbRelatedServicesPanel.BorderStyle = BorderStyle.FixedSingle;
        _dbRelatedServicesPanel.Padding = new Padding(8);
        _dbRelatedServicesPanel.Margin = new Padding(0);
        _dbPreset.Margin = new Padding(0, 2, 0, 4);
        _dbPreset.ForeColor = TitleColor;
        _dbPreset.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _dbInstallHint.Margin = new Padding(0, 0, 0, 8);
        _dbInstallHint.ForeColor = MutedTextColor;
        dbPanel.Controls.Add(dbActions, 0, 0);
        dbPanel.Controls.Add(_dbPreset, 0, 1);
        dbPanel.Controls.Add(_dbInstallHint, 0, 2);
        dbPanel.Controls.Add(new Label { Text = "Related DB Services", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = TitleColor, Margin = new Padding(0, 8, 0, 8) }, 0, 3);
        dbPanel.Controls.Add(_dbRelatedServicesPanel, 0, 4);
        dbTab.Controls.Add(dbPanel);

        tabs.TabPages.Add(serverTab);
        tabs.TabPages.Add(dbTab);

        root.Controls.Add(topCard, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        Controls.Add(root);

        _busyControls.AddRange(new Control[] { _toggleService, _restartService, _savePortsButton });
    }

    private static GroupBox BuildGroupBox(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 12),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = TitleColor
        };

        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }

    private void AddAction(FlowLayoutPanel panel, string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            Width = 130,
            Height = 36,
            Margin = new Padding(0, 0, 8, 8)
        };
        StyleSecondaryButton(button);
        button.Click += (_, _) => action();
        panel.Controls.Add(button);
        _busyControls.Add(button);
    }

    private void WireEvents()
    {
        _toggleService.Click += async (_, _) =>
        {
            if (_isRunning)
            {
                await RunBusyAction(() => Task.Run(() => _manager.StopService()), "Service stopped.");
            }
            else
            {
                await RunBusyAction(StartServiceWithDbRecoveryAsync, "Service started.");
            }
        };
        _restartService.Click += async (_, _) => await RunBusyAction(RestartServiceAsync, "Service restarted.");

        _togglePostgreSql.Click += async (_, _) =>
        {
            if (_isPostgreSqlRunning)
            {
                await RunBusyAction(() => Task.Run(() => _manager.StopPostgreSqlService()), "PostgreSQL stopped.");
            }
            else
            {
                await RunBusyAction(() => Task.Run(() => _manager.StartPostgreSqlService()), "PostgreSQL started.");
            }
        };
        _restartPostgreSql.Click += async (_, _) =>
            await RunBusyAction(() => Task.Run(() => _manager.RestartPostgreSqlService()), "PostgreSQL restarted.");

        _savePortsButton.Click += async (_, _) => await SavePortsAsync();
        _apiPortInput.ValueChanged += (_, _) => _portInputsDirty = true;
        _webPortInput.ValueChanged += (_, _) => _portInputsDirty = true;
        _apiPortInput.Leave += (_, _) => _portInputsDirty = false;
        _webPortInput.Leave += (_, _) => _portInputsDirty = false;

        _refreshTimer.Tick += (_, _) => RefreshState();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void RefreshRelatedServices(IReadOnlyList<RelatedServiceStatus> services)
    {
        _serverRelatedServicesPanel.Controls.Clear();
        _dbRelatedServicesPanel.Controls.Clear();

        foreach (var service in services)
        {
            var statusLower = service.Status.ToLowerInvariant();
            var ok = statusLower.Contains("running") || statusLower.Contains("active");
            var label = new Label
            {
                AutoSize = true,
                Text = $"{service.Name}: {service.Status}",
                BackColor = ok ? Color.FromArgb(233, 248, 239) : Color.FromArgb(255, 237, 237),
                ForeColor = ok ? SuccessBorderColor : DangerBorderColor,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(0, 0, 8, 8)
            };

            var isDb = service.Name.Contains("postgres", StringComparison.OrdinalIgnoreCase) ||
                       service.Category.Contains("database", StringComparison.OrdinalIgnoreCase) ||
                       service.Category.Contains("windows service", StringComparison.OrdinalIgnoreCase);

            if (isDb)
            {
                _dbRelatedServicesPanel.Controls.Add(label);
            }
            else
            {
                _serverRelatedServicesPanel.Controls.Add(label);
            }
        }
    }

    private async Task SavePortsAsync()
    {
        var state = _manager.GetState();
        if (state.IsWebReachable || state.IsFrontendReachable)
        {
            MessageBox.Show("Stop service before changing API/Web UI ports.", "E_Att_Service", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RunBusyAction(() => Task.Run(() => _manager.SavePorts((int)_apiPortInput.Value, (int)_webPortInput.Value)), "Ports saved.");
        _portInputsDirty = false;
    }

    private void OpenApi()
    {
        var state = _manager.GetState();
        var url = $"http://127.0.0.1:{state.Port}/api/server/stats";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private async Task TestPortsAsync()
    {
        var state = _manager.GetState();
        var configuredFrontendPort = state.FrontendPort;
        var apiUrl = $"http://127.0.0.1:{state.Port}/api/server/stats";
        var webUrl = $"http://127.0.0.1:{configuredFrontendPort}/";

        var apiOk = await IsHttpReachableAsync(apiUrl).ConfigureAwait(true);
        var webOk = await IsHttpReachableAsync(webUrl).ConfigureAwait(true);
        MessageBox.Show(
            $"API ({state.Port}): {(apiOk ? "OK" : "Not reachable")}\nWeb UI ({configuredFrontendPort}): {(webOk ? "OK" : "Not reachable")}",
            "E_Att_Service - Port Test",
            MessageBoxButtons.OK,
            apiOk && webOk ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private Task<string> CheckConfiguredPortsAsync()
        => Task.Run(() => _manager.CheckConfiguredPorts());

    private async Task StartServiceWithDbRecoveryAsync()
    {
        try
        {
            await Task.Run(() => _manager.StartService());
            return;
        }
        catch (Exception ex)
        {
            var message = SanitizeMessage(ex.Message);
            var needsRecovery =
                NeedsAdminPasswordPrompt(message) ||
                message.Contains("PostgreSQL is not reachable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Connection failed on configured/fallback ports", StringComparison.OrdinalIgnoreCase);

            if (!needsRecovery)
            {
                throw;
            }
        }

        await EnsureDbReadyWithPromptAsync();
        await Task.Run(() => _manager.StartService());
    }

    private async Task RestartServiceAsync()
    {
        if (_isRunning)
        {
            await Task.Run(() => _manager.StopService());
        }

        await StartServiceWithDbRecoveryAsync();
    }

    private static async Task<bool> IsHttpReachableAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunBusyAction(Func<Task> action, string successMessage)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await action();
            MessageBox.Show(SanitizeMessage(successMessage), "E_Att_Service", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(SanitizeMessage(ex.Message), "E_Att_Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async Task RunBusyAction(Func<Task<string>> action, string fallbackSuccessMessage)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await action();
            var message = string.IsNullOrWhiteSpace(result) ? fallbackSuccessMessage : SanitizeMessage(result);
            MessageBox.Show(message, "E_Att_Service", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(SanitizeMessage(ex.Message), "E_Att_Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        _refreshTimer.Enabled = !busy;
        foreach (var control in _busyControls)
        {
            if (!control.IsDisposed)
            {
                control.Enabled = !busy;
            }
        }
    }

    private async Task<string> EnsureDbReadyWithPromptAsync()
    {
        try
        {
            return await Task.Run(() => _manager.EnsurePostgreSqlReadyWithReport());
        }
        catch (Exception ex)
        {
            var message = SanitizeMessage(ex.Message);
            if (!ShouldPromptForAdminPassword(message))
            {
                throw;
            }

            var input = PromptForPassword("PostgreSQL Admin Password", "Enter password for user 'postgres':");
            if (string.IsNullOrWhiteSpace(input))
            {
                throw;
            }

            return await Task.Run(() => _manager.EnsurePostgreSqlReadyWithReport(input));
        }
    }

    private async Task<string> InstallPostgreSqlOfflineWithFallbackAsync()
    {
        try
        {
            return await Task.Run(() => _manager.InstallPostgreSqlOffline());
        }
        catch (FileNotFoundException ex)
        {
            var selectInstaller = MessageBox.Show(
                $"{SanitizeMessage(ex.Message)}\n\nDo you want to select PostgreSQL offline installer manually?",
                "Offline Installer Not Found",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (selectInstaller != DialogResult.Yes)
            {
                return "Offline installation cancelled.";
            }

            using var picker = new OpenFileDialog
            {
                Title = "Select PostgreSQL Offline Installer",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false
            };

            if (picker.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(picker.FileName))
            {
                return "Offline installation cancelled.";
            }

            return await Task.Run(() => _manager.InstallPostgreSqlOffline(picker.FileName));
        }
    }

    private async Task<string> ConfigurePostgreSqlPresetsWithPromptAsync()
    {
        try
        {
            return await Task.Run(() => _manager.ConfigurePostgreSqlPresets());
        }
        catch (Exception ex)
        {
            var message = SanitizeMessage(ex.Message);
            var needsPrompt = ShouldPromptForAdminPassword(message);
            if (!needsPrompt)
            {
                throw;
            }

            var input = PromptForPassword(
                "PostgreSQL Admin Password",
                "Enter password for user 'postgres' to configure presets:");

            if (string.IsNullOrWhiteSpace(input))
            {
                return "Preset configuration cancelled.";
            }

            return await Task.Run(() => _manager.ConfigurePostgreSqlPresets(input));
        }
    }

    private async Task<string> InitializeDbWithPromptAsync()
    {
        var confirmation = MessageBox.Show(
            "Initialize DB will DELETE all current database data and create a clean new database.\n\nDo you want to continue?",
            "Confirm Initialize DB",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (confirmation != DialogResult.Yes)
        {
            return "Database initialization cancelled.";
        }

        var input = PromptForPassword(
            "PostgreSQL Admin Password",
            "Enter password for user 'postgres' to initialize database:");

        if (string.IsNullOrWhiteSpace(input))
        {
            return "Database initialization cancelled.";
        }

        return await Task.Run(() => _manager.InitializePostgreSqlDatabase(input));
    }

    private async Task<string> RepairDbLoginWithPromptAsync()
    {
        var input = PromptForPassword(
            "PostgreSQL Admin Password",
            "Enter password for user 'postgres' to repair PB_Attendance login:");

        if (string.IsNullOrWhiteSpace(input))
        {
            return "Database login repair cancelled.";
        }

        return await Task.Run(() => _manager.RepairPostgreSqlLogin(input));
    }

    private async Task<string> SetAppDbPasswordWithPromptAsync()
    {
        var adminPassword = PromptForPassword(
            "PostgreSQL Admin Password",
            "Enter password for user 'postgres':");

        if (string.IsNullOrWhiteSpace(adminPassword))
        {
            return "Password update cancelled.";
        }

        var appPassword = PromptForPassword(
            "App DB Password",
            "Enter new password for user 'E_Attendace':");

        if (string.IsNullOrWhiteSpace(appPassword))
        {
            return "Password update cancelled.";
        }

        return await Task.Run(() => _manager.SetApplicationDbPassword(adminPassword, appPassword));
    }

    private void RunSafe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(SanitizeMessage(ex.Message), "E_Att_Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string SanitizeMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = Regex.Replace(text, @"(?i)(password\s*=\s*)([^;,\s]+)", "$1***");
        sanitized = Regex.Replace(sanitized, @"(?i)(pwd\s*=\s*)([^;,\s]+)", "$1***");
        return sanitized;
    }

    private static bool NeedsAdminPasswordPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        return lower.Contains("password authentication failed") ||
               lower.Contains("28p01") ||
               lower.Contains("admin password is required");
    }

    private static bool ShouldPromptForAdminPassword(string text)
    {
        if (NeedsAdminPasswordPrompt(text))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return
            text.Contains("Connection failed on configured/fallback ports", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("PostgreSQL is not reachable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Could not repair PostgreSQL login", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("admin password", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PromptForPassword(string title, string prompt)
    {
        using var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            Width = 420,
            Height = 170
        };

        var label = new Label { Left = 14, Top = 14, Width = 380, Text = prompt };
        var input = new TextBox { Left = 14, Top = 40, Width = 380, UseSystemPasswordChar = true };
        var ok = new Button { Text = "OK", Left = 238, Width = 75, Top = 78, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 319, Width = 75, Top = 78, DialogResult = DialogResult.Cancel };

        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(label);
        dialog.Controls.Add(input);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);

        return dialog.ShowDialog() == DialogResult.OK ? input.Text : null;
    }

    private void StyleStatusPill(Label label)
    {
        label.BackColor = SurfaceMutedColor;
        label.BorderStyle = BorderStyle.FixedSingle;
        label.Padding = new Padding(10, 5, 10, 5);
        label.Margin = new Padding(0, 0, 0, 6);
        label.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        label.ForeColor = TitleColor;
    }

    private void StylePortInput(NumericUpDown input)
    {
        input.BackColor = SurfaceColor;
        input.ForeColor = TitleColor;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        input.Height = 30;
        input.Margin = new Padding(0, 6, 8, 8);
    }

    private void StyleSecondaryButton(Button button, int? width = null)
    {
        StyleButton(button, SurfaceColor, TitleColor, BorderColor, width);
    }

    private void StylePrimaryButton(Button button, int? width = null)
    {
        StyleButton(button, PrimaryColor, Color.White, PrimaryBorderColor, width);
    }

    private void StyleRuntimeButton(Button button, bool isStopAction)
    {
        var back = isStopAction ? DangerColor : SuccessColor;
        var border = isStopAction ? DangerBorderColor : SuccessBorderColor;
        StyleButton(button, back, Color.White, border, width: 154);
    }

    private void StyleRuntimePostgreSqlButton()
    {
        StyleRuntimeButton(_togglePostgreSql, _isPostgreSqlRunning);
    }

    private void ApplyRuntimeButtonStyles()
    {
        StyleRuntimeButton(_toggleService, _isRunning);
        StyleRuntimePostgreSqlButton();
    }

    private static void StyleButton(Button button, Color backColor, Color foreColor, Color borderColor, int? width = null)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = borderColor;
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Height = 36;
        if (width.HasValue)
        {
            button.Width = width.Value;
        }
    }
}
