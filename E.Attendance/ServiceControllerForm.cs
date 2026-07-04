using System.Text.RegularExpressions;
using System.Drawing.Drawing2D;

namespace E.Attendance;

internal sealed class ServiceControllerForm : Form
{
    private static readonly Color BgPage     = Color.FromArgb(245, 247, 250);
    private static readonly Color BgCard     = Color.White;
    private static readonly Color BorderLine = Color.FromArgb(220, 225, 235);
    private static readonly Color AccentBlue = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentGreen= Color.FromArgb(22, 163, 74);
    private static readonly Color AccentRed  = Color.FromArgb(220, 38, 38);
    private static readonly Color AccentAmber= Color.FromArgb(217, 119, 6);
    private static readonly Color TextHead   = Color.FromArgb(17, 24, 39);
    private static readonly Color TextMuted  = Color.FromArgb(107, 114, 128);

    private readonly WebServiceManager _manager;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private bool _busy, _isRunning, _winSvcInstalled, _refreshing;

    private readonly PowerButton _powerBtn = new(BgPage);
    private readonly ServiceRow  _apiRow   = new(WebServiceManager.ApiWindowsServiceName);
    private readonly StatusPill  _dbPill   = new("DB",    AccentGreen, AccentRed);
    private readonly StatusPill  _pgPill   = new("PgSQL", AccentGreen, AccentRed);

    // Action buttons
    private readonly LightButton _btnDash      = new("⚡  Open Dashboard",    AccentBlue);
    private readonly LightButton _btnFixDb     = new("🔧  Fix & Verify DB",  AccentAmber);
    private readonly LightButton _btnPgRst     = new("↺   Restart PgSQL",    AccentBlue);
    private readonly LightButton _btnStop      = new("■   Stop All",         AccentRed);
    private readonly LightButton _btnInitDb    = new("🗄  Initialize DB",     AccentAmber);
    private readonly LightButton _btnSvcAction = new("⬇  Install Services",  AccentGreen); // text changes
    private readonly LightButton _btnDbConfig  = new("🗃  Database Config",   AccentBlue);

    // Port row
    private readonly NumericUpDown _apiPort = MakePort(WebServiceManager.ApiDefaultPort);
    private readonly NumericUpDown _webPort = MakePort(WebServiceManager.FrontendDefaultPort);
    private readonly LightButton   _btnSave = new("Save Ports", AccentBlue);

    private readonly CpuGraphControl _cpuGraph = new();
    private System.Diagnostics.PerformanceCounter? _cpuCounter;

    public ServiceControllerForm(WebServiceManager manager)
    {
        _manager       = manager;
        Text           = "E-Attendance  —  Service Controller";
        StartPosition  = FormStartPosition.CenterScreen;
        Size           = new Size(520, 544);
        MinimumSize    = new Size(480, 524);
        BackColor      = BgPage;
        ForeColor      = TextHead;
        Font           = new Font("Segoe UI", 9.5F);
        DoubleBuffered = true;
        ResizeRedraw   = true;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? Application.ExecutablePath); } catch { }
        Task.Run(() => { try { _cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total"); } catch { } });
        Build(); Wire(); _ = RefreshAsync(); _timer.Start();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void Build()
    {
        // 10 rows, no DB stats row
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10,
            BackColor = BgPage, Padding = new Padding(12, 4, 12, 4)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // header
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));   // separator
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); // hero
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // cpu graph
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // service row
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // row: dashboard | fix db
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // row: restart pg | stop all
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // row: initialize db (full)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));  // row: svc action | db config
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // port row

        // Header
        root.Controls.Add(new Label
        {
            Text = "E-Attendance  —  Service Controller",
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
            ForeColor = TextHead, AutoSize = false,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = BorderLine }, 0, 1);

        // Hero (power button + status pills)
        var hero = new Panel { Dock = DockStyle.Fill, BackColor = BgPage };
        hero.Controls.AddRange(new Control[] { _powerBtn, _dbPill, _pgPill });
        hero.Resize += (_, _) => LayoutHero(hero);
        LayoutHero(hero);
        root.Controls.Add(hero, 0, 2);

        // CPU graph
        _cpuGraph.Dock = DockStyle.Fill;
        root.Controls.Add(_cpuGraph, 0, 3);

        // Service status row
        var svcCard = new CardPanel { Dock = DockStyle.Fill, BackColor = BgCard };
        _apiRow.Dock = DockStyle.Fill; _apiRow.Margin = new Padding(8, 2, 8, 2);
        svcCard.Controls.Add(_apiRow);
        root.Controls.Add(svcCard, 0, 4);

        // Button rows — all equal 2-button layout
        root.Controls.Add(MakeRow2(_btnDash, _btnFixDb), 0, 5);
        root.Controls.Add(MakeRow2(_btnPgRst, _btnStop), 0, 6);
        root.Controls.Add(MakeRowFull(_btnInitDb), 0, 7);
        root.Controls.Add(MakeRow2(_btnSvcAction, _btnDbConfig), 0, 8);  // always 2 equal buttons

        // Port row
        var pr = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = BgPage, Padding = Padding.Empty
        };
        var apiLbl = MkLbl("API Port", 8F, FontStyle.Regular, TextMuted, Point.Empty);
        apiLbl.Margin = new Padding(4, 10, 2, 0); pr.Controls.Add(apiLbl);
        _apiPort.Width = 65; _apiPort.Margin = new Padding(0, 6, 0, 0); pr.Controls.Add(_apiPort);
        var webLbl = MkLbl("Web Port", 8F, FontStyle.Regular, TextMuted, Point.Empty);
        webLbl.Margin = new Padding(8, 10, 2, 0); pr.Controls.Add(webLbl);
        _webPort.Width = 65; _webPort.Margin = new Padding(0, 6, 0, 0); pr.Controls.Add(_webPort);
        _btnSave.Height = 32; _btnSave.Width = 90; _btnSave.Margin = new Padding(10, 4, 0, 0);
        pr.Controls.Add(_btnSave);
        Resize += (_, _) => _btnSave.Width = Math.Max(ClientSize.Width - _btnSave.Left - 16, 90);
        root.Controls.Add(pr, 0, 9);

        Controls.Add(root);
    }

    // 2-button row: always equal halves, consistent spacing
    private static FlowLayoutPanel MakeRow2(LightButton a, LightButton b)
    {
        var f = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = BgPage, Padding = new Padding(0, 2, 0, 2)
        };
        a.Dock = b.Dock = DockStyle.None;
        a.Height = b.Height = 40;
        a.Margin = new Padding(0, 0, 6, 0);
        b.Margin = Padding.Empty;
        f.Resize += (_, _) =>
        {
            var w = (f.ClientSize.Width - 6) / 2;
            a.Width = b.Width = Math.Max(w, 10);
        };
        f.Controls.Add(a); f.Controls.Add(b);
        return f;
    }

    // Full-width single button row
    private static FlowLayoutPanel MakeRowFull(LightButton a)
    {
        var f = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = BgPage, Padding = new Padding(0, 2, 0, 2)
        };
        a.Height = 40; a.Margin = Padding.Empty;
        f.Resize += (_, _) => a.Width = Math.Max(f.ClientSize.Width, 10);
        f.Controls.Add(a);
        return f;
    }

    private static Label MkLbl(string t, float sz, FontStyle fs, Color c, Point p)
        => new() { Text = t, Font = new Font("Segoe UI", sz, fs), ForeColor = c, AutoSize = true, Location = p };

    private void LayoutHero(Panel hero)
    {
        var size = Math.Clamp(Math.Min(hero.Width, hero.Height) - 4, 80, 115);
        _powerBtn.Size     = new Size(size, size);
        _powerBtn.Location = new Point((hero.Width - _powerBtn.Width) / 2, 8);
        _dbPill.Location   = new Point(_powerBtn.Left - _dbPill.Width - 12, _powerBtn.Top + (_powerBtn.Height - _dbPill.Height) / 2 + 5);
        _pgPill.Location   = new Point(_powerBtn.Right + 12,                _powerBtn.Top + (_powerBtn.Height - _pgPill.Height) / 2 + 5);
    }

    private static NumericUpDown MakePort(int v) => new()
    {
        Minimum = 1, Maximum = 65535, Value = v,
        BackColor = Color.FromArgb(249, 250, 251), ForeColor = TextHead,
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center, Height = 30
    };

    // ── Wiring ────────────────────────────────────────────────────────────────

    private void Wire()
    {
        // Power button: full start/stop sequence
        _powerBtn.Click += async (_, _) => await RunBusy(async () =>
        {
            if (_isRunning)
                await Task.Run(() => _manager.NuclearStop());
            else
                await StartAllServicesAsync();
        });

        // Service row start/stop
        _apiRow.OnStart += async () => await RunBusy(async () =>
        {
            if (!_winSvcInstalled)
                await StartAllServicesAsync();
            else
            {
                await Task.Run(() =>
                {
                    EnsurePostgreSqlBeforeApiStart();
                    _manager.StartWindowsService(WebServiceManager.ApiWindowsServiceName);
                });
            }
        });
        _apiRow.OnStop += async () => await RunBusy(() =>
            Task.Run(() => _manager.StopWindowsService(WebServiceManager.ApiWindowsServiceName)));

        // Other buttons
        _btnDash.Click   += (_, _) => Safe(() => _manager.OpenDashboard());
        _btnFixDb.Click  += async (_, _) => await RunBusy(async () =>
        {
            var r = await Task.Run(() => { _manager.EnsureDefaults(); return _manager.FixDatabaseSchema(); });
            ShowScrollableReport("Fix & Verify Database Report", r);
        });
        _btnPgRst.Click  += async (_, _) => await RunBusy(() => Task.Run(() => _manager.RestartPostgreSqlService()));
        _btnStop.Click   += async (_, _) => await RunBusy(() => Task.Run(() => _manager.NuclearStop()));
        _btnInitDb.Click += async (_, _) => await InitDbAsync();

        // _btnSvcAction: text/action toggles based on whether services are installed
        _btnSvcAction.Click += async (_, _) =>
        {
            if (!_winSvcInstalled)
            {
                await RunBusy(async () =>
                {
                    var (ok, msg) = await Task.Run(() => _manager.InstallWindowsServices());
                    MessageBox.Show(msg, ok ? "Services Installed" : "Install Failed",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    if (ok) await StartAllServicesAsync();
                });
            }
            else
            {
                var confirm = MessageBox.Show(
                    "This will stop all services, reinstall them, and start fresh.\n\nContinue?",
                    "Fix Services", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.Yes) return;
                await RunBusy(async () =>
                {
                    await Task.Run(() => { try { _manager.NuclearStop(); } catch { } });
                    var (ok, msg) = await Task.Run(() => _manager.InstallWindowsServices());
                    MessageBox.Show(msg, ok ? "Services Fixed" : "Fix Failed",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    if (ok) await StartAllServicesAsync();
                });
            }
        };

        _btnDbConfig.Click += async (_, _) => await RunBusy(async () => await ShowDatabaseConfigDialog());

        _btnSave.Click += async (_, _) =>
        {
            var a = (int)_apiPort.Value;
            var w = (int)_webPort.Value;
            if (a == w) { MessageBox.Show("API Port and Web Port cannot be the same.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (_isRunning) { MessageBox.Show("Stop all services before changing ports.", "Service Running", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            await RunBusy(async () => await Task.Run(() => _manager.SavePorts(a, w)));
            MessageBox.Show("Port settings saved.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        _timer.Tick  += (_, _) => _ = RefreshAsync();
        FormClosing  += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    // ── Service start sequence ─────────────────────────────────────────────────

    /// <summary>Full ordered start: ensure PostgreSQL → fix DB schema → start API.</summary>
    private async Task StartAllServicesAsync()
    {
        await Task.Run(() =>
        {
            // 1. Ensure defaults (connection settings, paths)
            _manager.EnsureDefaults();

            // 2. Start PostgreSQL and wait for it to be ready
            EnsurePostgreSqlBeforeApiStart();

            // 3. Auto-fix database schema (create missing tables, etc.)
            try { _manager.FixDatabaseSchemaInternal(); } catch { /* non-fatal */ }

            // 4. Start API service (Windows service or direct process)
            if (_winSvcInstalled)
                _manager.StartWindowsService(WebServiceManager.ApiWindowsServiceName);
            else
                _manager.StartService();
        });
    }

    /// <summary>
    /// Ensures PostgreSQL is running and accepting connections before starting the API.
    /// Polls up to 60 seconds so even a cold Windows boot has time to get PG ready.
    /// </summary>
    private void EnsurePostgreSqlBeforeApiStart()
    {
        // Start PostgreSQL if it's not running
        try { _manager.EnsurePostgreSqlServiceRunning(); } catch { }

        // Poll until PostgreSQL accepts connections (up to 60 s)
        for (var i = 0; i < 60; i++)
        {
            try
            {
                var (ok, _) = _manager.TestPostgreSqlConnection();
                if (ok) return;
            }
            catch { }
            System.Threading.Thread.Sleep(1000);
        }
        // Continue regardless — the web app has its own internal retry logic
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (_busy || _refreshing || IsDisposed) return;
        _refreshing = true;
        try
        {
            var s = await Task.Run(() => _manager.GetState());
            _winSvcInstalled = s.ApiWinSvcInstalled;
            var apiWinRunning = s.ApiWinSvcInstalled && s.ApiWinSvcStatus.Equals("Running", StringComparison.OrdinalIgnoreCase);
            _isRunning = s.IsWebReachable || apiWinRunning;
            var pgOn = s.PostgresStatus.Contains("running", StringComparison.OrdinalIgnoreCase)
                    || s.PostgresStatus.Contains("connected", StringComparison.OrdinalIgnoreCase);
            var dbOn = s.PostgresStatus.Contains("Connected", StringComparison.OrdinalIgnoreCase);

            _powerBtn.SetState(_isRunning, _busy);
            if (_cpuCounter != null) try { _cpuGraph.AddValue(_cpuCounter.NextValue()); } catch { }

            _apiRow.SetState(
                s.ApiWinSvcInstalled,
                s.ApiWinSvcInstalled ? s.ApiWinSvcStatus : (s.IsWebReachable ? "Running" : "Stopped"),
                s.Port);
            _dbPill.SetState(dbOn, "");
            _pgPill.SetState(pgOn, "");

            // Toggle Install / Fix Services button text based on installed state
            _btnSvcAction.UpdateText(_winSvcInstalled ? "🔧  Fix Services" : "⬇  Install Services");
            _btnSvcAction.UpdateAccent(_winSvcInstalled ? AccentAmber : AccentGreen);

            var canEdit = !_isRunning && !_busy;
            _apiPort.Enabled = _webPort.Enabled = _btnSave.Enabled = canEdit;
            if (!_apiPort.Focused && !_webPort.Focused) { _apiPort.Value = s.Port; _webPort.Value = s.FrontendPort; }
        }
        catch { }
        finally { _refreshing = false; }
    }

    // ── Busy lock ─────────────────────────────────────────────────────────────

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _powerBtn.SetState(_isRunning, true);
        var buttons = new Control[] { _btnDash, _btnFixDb, _btnPgRst, _btnStop, _btnInitDb, _btnSvcAction, _btnDbConfig, _btnSave, _apiPort, _webPort };
        foreach (var b in buttons) b.Enabled = false;
        _apiRow.Enabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Sanitize(ex.Message), "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            foreach (var b in buttons) b.Enabled = true;
            _apiRow.Enabled = true;
            await RefreshAsync();
        }
    }

    // ── Dialogs ───────────────────────────────────────────────────────────────

    private async Task InitDbAsync()
    {
        if (MessageBox.Show(
            "⚠  This will DELETE all existing data and recreate the database from scratch.\n\nAre you sure?",
            "Initialize Database", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes) return;

        var pw = PromptPassword("PostgreSQL Admin Password", "Enter password for the 'postgres' admin user:");
        if (string.IsNullOrWhiteSpace(pw)) return;

        await RunBusy(async () =>
        {
            var result = await Task.Run(() => _manager.InitializePostgreSqlDatabase(pw));
            MessageBox.Show(result, "Database Initialized", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private static void ShowScrollableReport(string title, string text)
    {
        using var dlg = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            Width = 580, Height = 520, BackColor = Color.White,
            MinimumSize = new Size(400, 300)
        };
        var txt = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(248, 250, 252), ForeColor = Color.FromArgb(17, 24, 39),
            BorderStyle = BorderStyle.None, Text = text
        };
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.White };
        var ok = new Button
        {
            Text = "Close", Width = 90, Height = 34,
            DialogResult = DialogResult.OK,
            BackColor = AccentBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Location = new Point(panel.Width - ok.Width - 12, 7);
        panel.Resize += (_, _) => ok.Location = new Point(panel.Width - ok.Width - 12, 7);
        panel.Controls.Add(ok);
        dlg.Controls.Add(txt);
        dlg.Controls.Add(panel);
        dlg.AcceptButton = ok;
        dlg.ShowDialog();
    }

    private static string? PromptPassword(string title, string prompt)
    {
        using var dlg = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            Width = 420, Height = 165, BackColor = Color.White
        };
        var lbl = new Label { Left = 14, Top = 14, Width = 380, Text = prompt, ForeColor = TextMuted };
        var txt = new TextBox { Left = 14, Top = 40, Width = 380, UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle };
        var ok  = new Button { Text = "OK",     Left = 228, Width = 80, Top = 78, DialogResult = DialogResult.OK,     BackColor = AccentBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var can = new Button { Text = "Cancel", Left = 316, Width = 80, Top = 78, DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(229, 231, 235), ForeColor = TextHead, FlatStyle = FlatStyle.Flat };
        ok.FlatAppearance.BorderSize = can.FlatAppearance.BorderSize = 0;
        dlg.AcceptButton = ok; dlg.CancelButton = can;
        dlg.Controls.AddRange(new Control[] { lbl, txt, ok, can });
        return dlg.ShowDialog() == DialogResult.OK ? txt.Text : null;
    }

    private async Task ShowDatabaseConfigDialog()
    {
        var connStr = _manager.GetPostgreSqlConnectionString();
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connStr);

        using var dlg = new Form
        {
            Text = "Database Configuration", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            Width = 480, Height = 420, BackColor = Color.White
        };

        var lblTitle = new Label { Text = "PostgreSQL Connection Settings", Font = new Font("Segoe UI", 10F, FontStyle.Bold), ForeColor = TextHead, Left = 14, Top = 14, Width = 440, Height = 30 };
        var lblHost  = new Label { Text = "Host",     Left = 14, Top = 54,  Width = 100, ForeColor = TextMuted };
        var txtHost  = new TextBox { Left = 120, Top = 50,  Width = 330, Text = builder.Host,   BorderStyle = BorderStyle.FixedSingle };
        var lblPort  = new Label { Text = "Port",     Left = 14, Top = 88,  Width = 100, ForeColor = TextMuted };
        var txtPort  = new NumericUpDown { Left = 120, Top = 84,  Width = 100, Minimum = 1, Maximum = 65535, Value = builder.Port > 0 ? builder.Port : 1277 };
        var lblDb    = new Label { Text = "Database", Left = 14, Top = 122, Width = 100, ForeColor = TextMuted };
        var txtDb    = new TextBox { Left = 120, Top = 118, Width = 330, Text = builder.Database, BorderStyle = BorderStyle.FixedSingle };
        var lblUser  = new Label { Text = "Username", Left = 14, Top = 156, Width = 100, ForeColor = TextMuted };
        var txtUser  = new TextBox { Left = 120, Top = 152, Width = 330, Text = builder.Username, BorderStyle = BorderStyle.FixedSingle };
        var lblPass  = new Label { Text = "Password", Left = 14, Top = 190, Width = 100, ForeColor = TextMuted };
        var txtPass  = new TextBox { Left = 120, Top = 186, Width = 330, Text = new string('●', 12), UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle };
        string originalPassword = builder.Password ?? string.Empty; bool passwordChanged = false;
        txtPass.Enter += (_, _) => { if (!passwordChanged) { txtPass.Text = ""; passwordChanged = true; } };

        var btnTest = new Button { Text = "Test Connection", Left = 120, Top = 228, Width = 130, Height = 32, BackColor = AccentBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        btnTest.FlatAppearance.BorderSize = 0;
        btnTest.Click += async (_, _) =>
        {
            btnTest.Enabled = false; btnTest.Text = "Testing…";
            try
            {
                var tb = new Npgsql.NpgsqlConnectionStringBuilder
                {
                    Host = txtHost.Text?.Trim() ?? string.Empty, Port = (int)txtPort.Value,
                    Database = txtDb.Text?.Trim() ?? string.Empty, Username = txtUser.Text?.Trim() ?? string.Empty,
                    Password = passwordChanged ? txtPass.Text : originalPassword,
                    Timeout = 5, CommandTimeout = 5
                };
                using var c = new Npgsql.NpgsqlConnection(tb.ConnectionString);
                await c.OpenAsync();
                MessageBox.Show("✓  Connection successful!", "Test Result", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show($"Connection failed:\n{ex.Message}", "Test Result", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { btnTest.Enabled = true; btnTest.Text = "Test Connection"; }
        };

        var btnSave   = new Button { Text = "Save",   Left = 260, Top = 228, Width = 90, Height = 32, BackColor = AccentGreen, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Cancel", Left = 360, Top = 228, Width = 90, Height = 32, BackColor = Color.FromArgb(229, 231, 235), ForeColor = TextHead, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
        btnSave.FlatAppearance.BorderSize = btnCancel.FlatAppearance.BorderSize = 0;

        var lblInfo = new Label { Text = "Password is hidden. Leave as dots to keep the current password.", Left = 14, Top = 275, Width = 440, Height = 36, ForeColor = TextMuted, Font = new Font("Segoe UI", 8F) };

        dlg.AcceptButton = btnSave; dlg.CancelButton = btnCancel;
        dlg.Controls.AddRange(new Control[] { lblTitle, lblHost, txtHost, lblPort, txtPort, lblDb, txtDb, lblUser, txtUser, lblPass, txtPass, btnTest, btnSave, btnCancel, lblInfo });

        if (dlg.ShowDialog() == DialogResult.OK)
        {
            var nb = new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = txtHost.Text?.Trim() ?? string.Empty, Port = (int)txtPort.Value,
                Database = txtDb.Text?.Trim() ?? string.Empty, SearchPath = "E_Attendace",
                Username = txtUser.Text?.Trim() ?? string.Empty,
                Password = passwordChanged ? (txtPass.Text ?? string.Empty) : originalPassword,
                IncludeErrorDetail = true, MaxPoolSize = 100, ConnectionLifetime = 300
            };
            _manager.UpdatePostgreSqlConnection(nb.ConnectionString);
            MessageBox.Show("Database settings saved.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void RefreshState() => _ = RefreshAsync();

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { MessageBox.Show(Sanitize(ex.Message), "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = Regex.Replace(s, @"(?i)(password\s*=\s*)([^;,\s]+)", "$1***");
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}

// ── Custom Controls ────────────────────────────────────────────────────────────

internal sealed class CpuGraphControl : Control
{
    private readonly List<float> _values = new();
    private const int MaxPts = 100;
    public CpuGraphControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        for (int i = 0; i < MaxPts; i++) _values.Add(0);
    }
    public void AddValue(float v) { _values.RemoveAt(0); _values.Add(v); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 20 || Height < 10) return;
        using (var pen = new Pen(Color.FromArgb(40, 37, 99, 235), 1f))
            for (int i = 0; i <= 4; i++) { float y = i * (Height - 1) / 4f; g.DrawLine(pen, 0, y, Width, y); }
        if (_values.Count < 2) return;
        float dx = (float)Width / (MaxPts - 1);
        using (var path = new GraphicsPath())
        {
            path.StartFigure();
            path.AddLine(0, Height, 0, Height - (_values[0] / 100f * Height));
            for (int i = 1; i < _values.Count; i++)
                path.AddLine((i - 1) * dx, Height - (_values[i - 1] / 100f * Height), i * dx, Height - (_values[i] / 100f * Height));
            path.AddLine(Width, Height - (_values[^1] / 100f * Height), Width, Height);
            path.CloseFigure();
            using var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(80, 37, 99, 235), Color.Transparent, 90f);
            g.FillPath(brush, path);
        }
        using var linePen = new Pen(Color.FromArgb(37, 99, 235), 1.5f);
        for (int i = 1; i < _values.Count; i++)
            g.DrawLine(linePen, (i - 1) * dx, Height - (_values[i - 1] / 100f * Height), i * dx, Height - (_values[i] / 100f * Height));
    }
}

internal sealed class ServiceRow : Control
{
    private readonly string _name;
    private bool _installed; private string _status = "Not Installed"; private int _port;
    private readonly LightButton _btnStart = new("Start", Color.FromArgb(22, 163, 74));
    private readonly LightButton _btnStop  = new("Stop",  Color.FromArgb(220, 38, 38));
    public event Func<Task>? OnStart;
    public event Func<Task>? OnStop;

    public ServiceRow(string name)
    {
        _name = name;
        Size = new Size(440, 36);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _btnStart.Size = new Size(56, 22);
        _btnStop.Size  = new Size(56, 22);
        _btnStart.Click += async (_, _) => { if (OnStart != null) await OnStart(); };
        _btnStop.Click  += async (_, _) => { if (OnStop  != null) await OnStop();  };
        Controls.Add(_btnStart); Controls.Add(_btnStop);
        Resize += (_, _) => PositionButtons();
    }

    private void PositionButtons()
    {
        _btnStart.Location = new Point(Width - _btnStart.Width - 6, (Height - _btnStart.Height) / 2);
        _btnStop.Location  = new Point(Width - _btnStop.Width  - 6, (Height - _btnStop.Height)  / 2);
    }

    public void SetState(bool installed, string status, int port)
    {
        _installed = installed; _status = status; _port = port;
        var running = status.Equals("Running", StringComparison.OrdinalIgnoreCase);
        _btnStart.Visible = !running;   // show Start when stopped (even if not installed — lets user kick it)
        _btnStop.Visible  =  running;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _btnStart.Enabled = Enabled;
        _btnStop.Enabled  = Enabled;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var running = _status.Equals("Running", StringComparison.OrdinalIgnoreCase);
        var bg  = !_installed ? Color.FromArgb(249, 250, 251) : running ? Color.FromArgb(240, 253, 244) : Color.FromArgb(254, 242, 242);
        var dot = !_installed ? Color.FromArgb(156, 163, 175) : running ? Color.FromArgb(22, 163, 74)   : Color.FromArgb(220, 38, 38);
        using (var b = new SolidBrush(bg)) g.FillRounded(b, ClientRectangle, 7);
        using (var p = new Pen(Color.FromArgb(220, 225, 235), 1f)) g.DrawRounded(p, new Rectangle(0, 0, Width - 1, Height - 1), 7);
        using (var db = new SolidBrush(dot)) g.FillEllipse(db, 10, 14, 8, 8);
        using var nf = new Font("Segoe UI", 8F, FontStyle.Bold);
        using var nb = new SolidBrush(Color.FromArgb(31, 41, 55));
        g.DrawString(_name, nf, nb, 24, 4);
        using var sf = new Font("Segoe UI", 7F);
        using var sb = new SolidBrush(Color.FromArgb(107, 114, 128));
        g.DrawString(_installed ? $"{_status}  :{_port}" : "Not Installed", sf, sb, 24, 18);
    }
}

internal sealed class PowerButton : Control
{
    private bool _on, _busy; private float _angle; private readonly Color _bg;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 30 };
    private float _pulse; private bool _pulseDir = true;

    public PowerButton(Color bg)
    {
        _bg = bg; Size = new Size(120, 120); DoubleBuffered = true; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _anim.Tick += (_, _) =>
        {
            _angle = (_angle + 10f) % 360f;
            if (_on && !_busy)
            {
                if (_pulseDir) { _pulse += 0.05f; if (_pulse >= 1) _pulseDir = false; }
                else           { _pulse -= 0.05f; if (_pulse <= 0) _pulseDir = true;  }
            }
            else _pulse = 0;
            Invalidate();
        };
        _anim.Start();
    }

    public void SetState(bool on, bool busy) { _on = on; _busy = busy; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = Width / 2f, cy = Height / 2f, r = Math.Min(Width, Height) / 2f - 10f;
        var col = _busy ? Color.FromArgb(217, 119, 6) : _on ? Color.FromArgb(22, 163, 74) : Color.FromArgb(220, 38, 38);
        if (_on && !_busy)
        {
            var gr = r + 4 + (_pulse * 8);
            using var path = new GraphicsPath(); path.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
            using var pgb = new PathGradientBrush(path) { CenterColor = Color.FromArgb((int)(60 + _pulse * 50), col), SurroundColors = new[] { Color.Transparent } };
            g.FillPath(pgb, path);
        }
        if (_busy)
        {
            using var arc = new Pen(col, 4.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, _angle, 260f);
        }
        else
        {
            using var ring = new Pen(col, 3.5f); g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
        }
        float ir = r - 12f;
        using (var bgb = new SolidBrush(_bg)) g.FillEllipse(bgb, cx - ir, cy - ir, ir * 2, ir * 2);
        using (var ip = new Pen(col, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float pr = ir * 0.42f;
            g.DrawArc(ip, cx - pr, cy - pr, pr * 2, pr * 2, -230f, 280f);
            g.DrawLine(ip, cx, cy - pr * 0.9f, cx, cy - pr * 1.9f);
        }
        var lbl = _busy ? "BUSY" : _on ? "ON" : "OFF";
        using var f = new Font("Segoe UI", 9F, FontStyle.Bold);
        using var tb = new SolidBrush(col);
        var sz = g.MeasureString(lbl, f);
        g.DrawString(lbl, f, tb, cx - sz.Width / 2, cy + ir * 0.55f);
    }

    protected override void Dispose(bool d) { if (d) _anim.Dispose(); base.Dispose(d); }
}

internal sealed class StatusPill : Control
{
    private bool _on; private string _extra = ""; private readonly string _name; private readonly Color _onC, _offC;
    public StatusPill(string name, Color on, Color off)
    {
        _name = name; _onC = on; _offC = off; Size = new Size(85, 38); DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    public void SetState(bool on, string extra) { _on = on; _extra = extra; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var ac = _on ? _onC : _offC;
        var bg = _on ? Color.FromArgb(240, 253, 244) : Color.FromArgb(254, 242, 242);
        var bo = _on ? Color.FromArgb(187, 247, 208) : Color.FromArgb(254, 202, 202);
        using (var b = new SolidBrush(bg)) g.FillRounded(b, ClientRectangle, 8);
        using (var p = new Pen(bo, 1f)) g.DrawRounded(p, new Rectangle(0, 0, Width - 1, Height - 1), 8);
        using (var dot = new SolidBrush(ac)) g.FillEllipse(dot, 8, 14, 7, 7);
        using var nf = new Font("Segoe UI", 7.5F, FontStyle.Bold);
        using var nb = new SolidBrush(Color.FromArgb(31, 41, 55));
        g.DrawString(_name, nf, nb, 18, 5);
        using var sf = new Font("Segoe UI", 6.5F);
        using var sb = new SolidBrush(Color.FromArgb(107, 114, 128));
        g.DrawString((_on ? "ON" : "OFF") + _extra, sf, sb, 18, 20);
    }
}

internal sealed class LightButton : Control
{
    private Color _ac; private bool _hov, _press;

    public LightButton(string text, Color accent)
    {
        _ac = accent; Text = text; DoubleBuffered = true; Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void UpdateText(string text)   { Text = text; Invalidate(); }
    public void UpdateAccent(Color color) { _ac = color; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hov = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hov = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)  { if (e.Button == MouseButtons.Left) { _press = true;  Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)    { _press = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = _press ? Color.FromArgb(30, _ac) : _hov ? Color.FromArgb(18, _ac) : Color.FromArgb(10, _ac);
        using (var b = new SolidBrush(bg))     g.FillRounded(b, ClientRectangle, 8);
        using (var p = new Pen(Color.FromArgb(Enabled ? 80 : 30, _ac), 1f))
            g.DrawRounded(p, new Rectangle(0, 0, Width - 1, Height - 1), 8);
        using var tf = new SolidBrush(Enabled ? _ac : Color.FromArgb(180, _ac));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, tf, ClientRectangle, sf);
    }
}

internal sealed class CardPanel : Panel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(BackColor)) g.FillRounded(b, ClientRectangle, 8);
        using (var p = new Pen(Color.FromArgb(220, 225, 235), 1f))
            g.DrawRounded(p, new Rectangle(0, 0, Width - 1, Height - 1), 8);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRounded(this Graphics g, Brush b, Rectangle r, int radius)
    {
        using var path = RoundedPath(r, radius); g.FillPath(b, path);
    }
    public static void DrawRounded(this Graphics g, Pen p, Rectangle r, int radius)
    {
        using var path = RoundedPath(r, radius); g.DrawPath(p, path);
    }
    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath(); int d = radius * 2;
        path.AddArc(r.X,             r.Y,              d, d, 180, 90);
        path.AddArc(r.Right - d,     r.Y,              d, d, 270, 90);
        path.AddArc(r.Right - d,     r.Bottom - d,     d, d,   0, 90);
        path.AddArc(r.X,             r.Bottom - d,     d, d,  90, 90);
        path.CloseFigure();
        return path;
    }
}
