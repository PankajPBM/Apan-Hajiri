using System.ComponentModel;
using System.IO;
using System.Net.Http;
using E.Attendance.Core.Models;
using E.Attendance.Data;
using E.Attendance.Services;

namespace E.Attendance;

public partial class Form1 : Form
{
    private readonly AttendanceService _service;

    private readonly BindingList<EmployeeRow> _employeeRows = new();
    private readonly BindingList<DeviceRow> _deviceRows = new();
    private readonly BindingList<AttendanceRow> _attendanceRows = new();

    private readonly DataGridView _employeeGrid = new();
    private readonly DataGridView _deviceGrid = new();
    private readonly DataGridView _attendanceGrid = new();

    private readonly ComboBox _employeePicker = new();
    private readonly ComboBox _devicePicker = new();
    private readonly CalendarDatePicker _fromPicker = new();
    private readonly CalendarDatePicker _toPicker = new();
    private readonly ComboBox _calendarTypePicker = new();
    private readonly Button _holidaySettingsButton = new();
    private Dictionary<DateOnly, string> _holidayMap = [];

    private readonly TextBox _deviceSearchBox = new();
    private readonly TextBox _deviceCommandBox = new();
    private readonly Label _serverStatusLabel = new();
    private readonly Label _requestHitsLabel = new();
    private readonly Label _lastRequestLabel = new();
    private readonly TextBox _serverPortBox = new();
    private readonly Button _startServerButton = new();
    private readonly Button _stopServerButton = new();
    private readonly Button _testServerButton = new();
    private readonly ZkPushServer _server;
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private int _requestHitCount;
    private bool _serverEventsWired;

    public Form1()
    {
        InitializeComponent();

        Text = "E-Attendance";
        Width = 1100;
        Height = 700;

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "E_Attendance",
            "pb-attendance.db");
        var repository = new SqliteAttendanceRepository(databasePath);
        var protocolService = new DeviceProtocolService();
        _service = new AttendanceService(repository, protocolService);
        _server = new ZkPushServer(_service);
        _holidayMap = HolidayStore.LoadActiveMap();

        BuildLayout();
        _fromPicker.SelectedDate = DateTime.Today.AddDays(-7);
        _toPicker.SelectedDate = DateTime.Today;
        ReloadAll();
    }

    private void BuildLayout()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        tabs.TabPages.Add(BuildDevicesTab());
        tabs.TabPages.Add(BuildEmployeesTab());
        tabs.TabPages.Add(BuildAttendanceTab());

        Controls.Add(tabs);
    }

    private TabPage BuildEmployeesTab()
    {
        var page = new TabPage("Employees");

        var inputPanel = BuildEmployeeInputPanel();
        ConfigureGrid(_employeeGrid, _employeeRows);
        _employeeGrid.MultiSelect = true;

        page.Controls.Add(_employeeGrid);
        page.Controls.Add(inputPanel);

        return page;
    }

    private Control BuildEmployeeInputPanel()
    {
        var addButton = new Button { Text = "Add Employee", AutoSize = true };
        addButton.Click += (_, _) => OpenEmployeeEditor();

        var editButton = new Button { Text = "Edit Selected", AutoSize = true };
        editButton.Click += (_, _) => OpenEmployeeEditorForSelected();

        var transferButton = new Button { Text = "Transfer Selected", AutoSize = true };
        transferButton.Click += (_, _) => OpenEmployeeTransferDialog();

        var deactivateButton = new Button { Text = "Deactivate Selected", AutoSize = true };
        deactivateButton.Click += (_, _) =>
        {
            var selectedRows = GetSelectedEmployeeRows();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show("Select at least one employee.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TryAction(() =>
            {
                foreach (var row in selectedRows)
                {
                    _service.DeactivateEmployee(row.Id);
                }

                ReloadEmployees();
            });
        };

        var infoPanel = BuildFlowPanel();
        infoPanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Text = "Add or edit employees with editable Area and multiple Department assignments. Use Transfer Selected to move one or more users to a new Area + Department group."
        });

        var actionPanel = BuildFlowPanel();
        actionPanel.Controls.Add(addButton);
        actionPanel.Controls.Add(editButton);
        actionPanel.Controls.Add(transferButton);
        actionPanel.Controls.Add(deactivateButton);

        return BuildTopLayout(
            BuildGroupBox("Employee Assignments", infoPanel),
            BuildGroupBox("Actions", actionPanel));
    }

    private TabPage BuildAttendanceTab()
    {
        var page = new TabPage("Attendance");

        var inputPanel = BuildAttendanceInputPanel();
        ConfigureGrid(_attendanceGrid, _attendanceRows);

        page.Controls.Add(_attendanceGrid);
        page.Controls.Add(inputPanel);

        return page;
    }

    private Control BuildAttendanceInputPanel()
    {
        _employeePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _employeePicker.Width = 200;

        _devicePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _devicePicker.Width = 160;

        _fromPicker.Width = 220;
        _toPicker.Width = 220;
        _fromPicker.HolidayProvider = () => _holidayMap;
        _toPicker.HolidayProvider = () => _holidayMap;

        _calendarTypePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _calendarTypePicker.Items.Clear();
        _calendarTypePicker.Items.AddRange(["AD", "BS"]);
        _calendarTypePicker.SelectedIndex = 0;
        _calendarTypePicker.SelectedIndexChanged += (_, _) => SyncCalendarTypeFromPicker();

        _holidaySettingsButton.Text = "Holidays";
        _holidaySettingsButton.AutoSize = true;
        _holidaySettingsButton.BackColor = Color.FromArgb(240, 247, 255);
        _holidaySettingsButton.Click += (_, _) => OpenHolidaySettings();

        var clockInButton = new Button { Text = "Clock In", AutoSize = true };
        clockInButton.Click += (_, _) => ClockSelectedEmployee(true);

        var clockOutButton = new Button { Text = "Clock Out", AutoSize = true };
        clockOutButton.Click += (_, _) => ClockSelectedEmployee(false);

        var refreshButton = new Button { Text = "Refresh", AutoSize = true };
        refreshButton.Click += (_, _) => ReloadAttendance();

        var selectPanel = BuildFlowPanel();
        selectPanel.Controls.Add(BuildLabel("Device"));
        selectPanel.Controls.Add(_devicePicker);
        selectPanel.Controls.Add(BuildLabel("Employee"));
        selectPanel.Controls.Add(_employeePicker);

        var rangePanel = BuildFlowPanel();
        rangePanel.Controls.Add(BuildLabel("Calendar"));
        rangePanel.Controls.Add(_calendarTypePicker);
        rangePanel.Controls.Add(BuildLabel("From"));
        rangePanel.Controls.Add(_fromPicker);
        rangePanel.Controls.Add(BuildLabel("To"));
        rangePanel.Controls.Add(_toPicker);
        rangePanel.Controls.Add(_holidaySettingsButton);

        var actionPanel = BuildFlowPanel();
        actionPanel.Controls.Add(clockInButton);
        actionPanel.Controls.Add(clockOutButton);
        actionPanel.Controls.Add(refreshButton);

        return BuildTopLayout(
            BuildGroupBox("Selection", selectPanel),
            BuildGroupBox("Date Range", rangePanel),
            BuildGroupBox("Actions", actionPanel));
    }

    private TabPage BuildDevicesTab()
    {
        var page = new TabPage("Devices");

        var inputPanel = BuildDeviceInputPanel();
        ConfigureGrid(_deviceGrid, _deviceRows);

        page.Controls.Add(_deviceGrid);
        page.Controls.Add(inputPanel);

        return page;
    }

    private Control BuildDeviceInputPanel()
    {
        var serialBox = BuildTextBox(160);
        var nameBox = BuildTextBox(140);
        var ipBox = BuildTextBox(140);
        var locationBox = BuildTextBox(160);

        var addButton = new Button { Text = "Add Device", AutoSize = true };
        addButton.Click += (_, _) =>
        {
            TryAction(() =>
            {
                _service.RegisterDevice(serialBox.Text, nameBox.Text, ipBox.Text, locationBox.Text);
                serialBox.Clear();
                nameBox.Clear();
                ipBox.Clear();
                locationBox.Clear();
                ReloadDevices(_deviceSearchBox.Text);
            });
        };

        _deviceSearchBox.Width = 180;
        _deviceSearchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            e.SuppressKeyPress = true;
            TryAction(() => ReloadDevices(_deviceSearchBox.Text));
        };

        var searchButton = new Button { Text = "Search", AutoSize = true };
        searchButton.Click += (_, _) => TryAction(() => ReloadDevices(_deviceSearchBox.Text));

        var clearButton = new Button { Text = "Clear", AutoSize = true };
        clearButton.Click += (_, _) =>
        {
            _deviceSearchBox.Clear();
            ReloadDevices();
        };

        var connectButton = new Button { Text = "Connect", AutoSize = true };
        connectButton.Click += (_, _) =>
        {
            if (_deviceGrid.CurrentRow?.DataBoundItem is DeviceRow row)
            {
                _service.ConnectDevice(row.Id);
                ReloadDevices(_deviceSearchBox.Text);
            }
        };

        var disconnectButton = new Button { Text = "Disconnect", AutoSize = true };
        disconnectButton.Click += (_, _) =>
        {
            if (_deviceGrid.CurrentRow?.DataBoundItem is DeviceRow row)
            {
                _service.DisconnectDevice(row.Id);
                ReloadDevices(_deviceSearchBox.Text);
            }
        };

        _deviceCommandBox.Width = 240;
        var sendCommandButton = new Button { Text = "Send Command", AutoSize = true };
        sendCommandButton.Click += (_, _) =>
        {
            if (_deviceGrid.CurrentRow?.DataBoundItem is not DeviceRow row)
            {
                return;
            }

            TryAction(() =>
            {
                _service.QueueDeviceCommand(row.SerialNumber, _deviceCommandBox.Text);
                _deviceCommandBox.Clear();
            });
        };

        _serverStatusLabel.AutoSize = true;
        _serverStatusLabel.Text = "Server: stopped";

        _serverPortBox.Width = 80;
        _serverPortBox.Text = WebServiceManager.ApiDefaultPort.ToString();

        _startServerButton.Text = "Start Server";
        _startServerButton.AutoSize = true;
        _startServerButton.Click += (_, _) => StartServerFromUi();

        _stopServerButton.Text = "Stop Server";
        _stopServerButton.AutoSize = true;
        _stopServerButton.Enabled = false;
        _stopServerButton.Click += (_, _) => StopPushServer();

        _testServerButton.Text = "Test Connection";
        _testServerButton.AutoSize = true;
        _testServerButton.Click += async (_, _) => await TestServerPingAsync().ConfigureAwait(false);

        _requestHitsLabel.AutoSize = true;
        _requestHitsLabel.Text = "Hits: 0";

        _lastRequestLabel.AutoSize = true;
        _lastRequestLabel.Text = "Last: -";

        var searchPanel = BuildFlowPanel();
        searchPanel.Controls.Add(BuildLabel("Search"));
        searchPanel.Controls.Add(_deviceSearchBox);
        searchPanel.Controls.Add(searchButton);
        searchPanel.Controls.Add(clearButton);
        searchPanel.Controls.Add(connectButton);
        searchPanel.Controls.Add(disconnectButton);
        searchPanel.Controls.Add(_serverStatusLabel);

        var commandPanel = BuildFlowPanel();
        commandPanel.Controls.Add(BuildLabel("Cmd"));
        commandPanel.Controls.Add(_deviceCommandBox);
        commandPanel.Controls.Add(sendCommandButton);

        var addPanel = BuildFlowPanel();
        addPanel.Controls.Add(BuildLabel("SN"));
        addPanel.Controls.Add(serialBox);
        addPanel.Controls.Add(BuildLabel("Name"));
        addPanel.Controls.Add(nameBox);
        addPanel.Controls.Add(BuildLabel("IP"));
        addPanel.Controls.Add(ipBox);
        addPanel.Controls.Add(BuildLabel("Location"));
        addPanel.Controls.Add(locationBox);
        addPanel.Controls.Add(addButton);

        var serverPanel = BuildFlowPanel();
        serverPanel.Controls.Add(BuildLabel("Port"));
        serverPanel.Controls.Add(_serverPortBox);
        serverPanel.Controls.Add(_startServerButton);
        serverPanel.Controls.Add(_stopServerButton);
        serverPanel.Controls.Add(_testServerButton);
        serverPanel.Controls.Add(_serverStatusLabel);
        serverPanel.Controls.Add(_requestHitsLabel);
        serverPanel.Controls.Add(_lastRequestLabel);

        return BuildTopLayout(
            BuildGroupBox("Search", searchPanel),
            BuildGroupBox("Server", serverPanel),
            BuildGroupBox("Device Commands", commandPanel),
            BuildGroupBox("Add Device", addPanel));
    }

    private void ReloadAll()
    {
        ReloadEmployees();
        ReloadDevices();
        ReloadAttendance();
    }

    private void OpenEmployeeEditor()
    {
        using var editor = new EmployeeAssignmentForm(
            "Add Employee",
            includeIdentityFields: true,
            employeeCount: null,
            availableAreas: GetKnownAreas(),
            departmentsByArea: GetDepartmentsByArea());

        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        TryAction(() =>
        {
            _service.CreateEmployee(
                editor.EmployeeCode,
                editor.FullName,
                editor.Area,
                editor.SelectedDepartments,
                editor.PrimaryDepartment);
            ReloadEmployees();
        });
    }

    private void OpenEmployeeEditorForSelected()
    {
        var selectedRows = GetSelectedEmployeeRows();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show("Select an employee to edit.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (selectedRows.Count > 1)
        {
            MessageBox.Show("Select only one employee to edit.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var employee = _service.ListEmployees().FirstOrDefault(x => x.Id == selectedRows[0].Id);
        if (employee is null)
        {
            MessageBox.Show("Employee not found.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var editor = new EmployeeAssignmentForm(
            "Edit Employee",
            includeIdentityFields: true,
            employeeCount: null,
            availableAreas: GetKnownAreas(),
            departmentsByArea: GetDepartmentsByArea(),
            employeeCode: employee.EmployeeCode,
            fullName: employee.FullName,
            area: employee.Area,
            selectedDepartments: employee.Departments.Count > 0 ? employee.Departments : employee.GetDepartments().Select(d => d.Name).ToList(),
            primaryDepartment: employee.Department);

        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        TryAction(() =>
        {
            _service.UpdateEmployeeAssignments(
                employee.Id,
                editor.EmployeeCode,
                editor.FullName,
                editor.Area,
                editor.SelectedDepartments,
                editor.PrimaryDepartment);
            ReloadEmployees();
        });
    }

    private void OpenEmployeeTransferDialog()
    {
        var selectedRows = GetSelectedEmployeeRows();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show("Select at least one employee to transfer.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var selectedEmployees = _service.ListEmployees()
            .Where(x => selectedRows.Any(row => row.Id == x.Id))
            .ToList();
        var initialArea = selectedEmployees
            .Select(x => x.Area)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        var initialDepartments = selectedEmployees
            .SelectMany(x => x.Departments.Count > 0 ? x.Departments : x.GetDepartments().Select(d => d.Name))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var transfer = new EmployeeAssignmentForm(
            "Transfer Employees",
            includeIdentityFields: false,
            employeeCount: selectedRows.Count,
            availableAreas: GetKnownAreas(),
            departmentsByArea: GetDepartmentsByArea(),
            area: initialArea.Count == 1 ? initialArea[0] : string.Empty,
            selectedDepartments: initialDepartments,
            primaryDepartment: initialDepartments.FirstOrDefault());

        if (transfer.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        TryAction(() =>
        {
            _service.TransferEmployees(
                selectedRows.Select(x => x.Id),
                transfer.Area,
                transfer.SelectedDepartments,
                transfer.PrimaryDepartment);
            ReloadEmployees();
        });
    }

    private List<EmployeeRow> GetSelectedEmployeeRows()
    {
        var rows = _employeeGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(static row => row.DataBoundItem as EmployeeRow)
            .OfType<EmployeeRow>()
            .GroupBy(static row => row.Id)
            .Select(static group => group.First())
            .ToList();

        if (rows.Count == 0 && _employeeGrid.CurrentRow?.DataBoundItem is EmployeeRow current)
        {
            rows.Add(current);
        }

        return rows;
    }

    private List<string> GetKnownAreas()
    {
        return _service.ListEmployees()
            .Select(x => x.Area?.Trim() ?? string.Empty)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Dictionary<string, List<string>> GetDepartmentsByArea()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var employee in _service.ListEmployees())
        {
            var area = employee.Area?.Trim() ?? string.Empty;
            if (!map.TryGetValue(area, out var departments))
            {
                departments = [];
                map[area] = departments;
            }

            foreach (var deptName in employee.Departments.Count > 0 ? employee.Departments : employee.GetDepartments().Select(d => d.Name).ToList())
            {
                if (departments.Any(existing => string.Equals(existing, deptName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                departments.Add(deptName);
            }
        }

        foreach (var item in map.Values)
        {
            item.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    private void ReloadEmployees()
    {
        _employeeRows.Clear();

        foreach (var employee in _service.ListEmployees())
        {
            _employeeRows.Add(new EmployeeRow(employee));
        }

        _employeePicker.DataSource = _employeeRows.ToList();
        _employeePicker.DisplayMember = nameof(EmployeeRow.Display);
        _employeePicker.ValueMember = nameof(EmployeeRow.Id);
    }

    private void ReloadDevices(string? searchQuery = null)
    {
        _deviceRows.Clear();

        foreach (var device in _service.SearchDevices(searchQuery))
        {
            _deviceRows.Add(new DeviceRow(device));
        }

        _devicePicker.DataSource = _deviceRows.ToList();
        _devicePicker.DisplayMember = nameof(DeviceRow.Display);
        _devicePicker.ValueMember = nameof(DeviceRow.Id);
    }

    private void ReloadAttendance()
    {
        _attendanceRows.Clear();

        var from = DateOnly.FromDateTime(_fromPicker.SelectedDate);
        var to = DateOnly.FromDateTime(_toPicker.SelectedDate);

        foreach (var record in _service.ListAttendance(from, to))
        {
            var employee = _service.ListEmployees().FirstOrDefault(e => e.Id == record.EmployeeId);
            var device = _service.ListDevices().FirstOrDefault(d => d.Id == record.DeviceId)
                ?? _service.ListDevices().FirstOrDefault(d => string.Equals(d.SerialNumber, record.DeviceSerialNumber, StringComparison.OrdinalIgnoreCase));

            _attendanceRows.Add(new AttendanceRow(record, employee, device));
        }
    }

    private void SyncCalendarTypeFromPicker()
    {
        var type = _calendarTypePicker.SelectedIndex == 1 ? CalendarType.BS : CalendarType.AD;
        _fromPicker.CalendarType = type;
        _toPicker.CalendarType = type;
    }

    private void OpenHolidaySettings()
    {
        using var manager = new HolidayManagerForm();
        if (manager.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _holidayMap = HolidayStore.LoadActiveMap();
        _fromPicker.HolidayProvider = () => _holidayMap;
        _toPicker.HolidayProvider = () => _holidayMap;
    }

    private void ClockSelectedEmployee(bool isClockIn)
    {
        if (_employeePicker.SelectedItem is not EmployeeRow employee)
        {
            return;
        }

        Guid? deviceId = null;
        if (_devicePicker.SelectedItem is DeviceRow device)
        {
            deviceId = device.Id;
        }

        TryAction(() =>
        {
            if (isClockIn)
            {
                _service.ClockIn(employee.Id, deviceId);
            }
            else
            {
                _service.ClockOut(employee.Id, deviceId);
            }

            ReloadAttendance();
        });
    }

    private static FlowLayoutPanel BuildFlowPanel()
        => new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

    private static GroupBox BuildGroupBox(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            Margin = new Padding(6)
        };

        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }

    private static Control BuildTopLayout(params Control[] groups)
    {
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        foreach (var group in groups)
        {
            if (group is GroupBox box)
            {
                box.MinimumSize = new Size(240, 0);
            }

            layout.Controls.Add(group);
        }

        return layout;
    }

    private static Label BuildLabel(string text)
        => new()
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 6, 6, 0)
        };

    private static TextBox BuildTextBox(int width)
        => new()
        {
            Width = width
        };

    private static void ConfigureGrid<T>(DataGridView grid, BindingList<T> dataSource)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AutoGenerateColumns = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.DataSource = dataSource;
    }

    private void TryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed class EmployeeRow
    {
        public EmployeeRow(Employee employee)
        {
            Id = employee.Id;
            EmployeeCode = employee.EmployeeCode;
            FullName = employee.FullName;
            Area = employee.Area;
            Department = employee.Department;
            Departments = string.Join(", ", employee.GetDepartments());
            IsActive = employee.IsActive;
        }

        [Browsable(false)]
        public Guid Id { get; }
        public string EmployeeCode { get; }
        public string FullName { get; }
        public string Area { get; }
        public string Department { get; }
        public string Departments { get; }
        public bool IsActive { get; }

        [Browsable(false)]
        public string Display => $"{EmployeeCode} - {FullName}";
    }

    private sealed class DeviceRow
    {
        public DeviceRow(Device device)
        {
            Id = device.Id;
            SerialNumber = device.SerialNumber;
            Name = device.Name;
            IpAddress = device.IpAddress;
            Location = device.Location;
            LastSeenUtc = device.LastSeenUtc;
            IsOnline = device.IsOnline && (!device.LastSeenUtc.HasValue || DateTime.UtcNow - device.LastSeenUtc.Value < TimeSpan.FromMinutes(5));
        }

        public Guid Id { get; }
        public string SerialNumber { get; }
        public string Name { get; }
        public string IpAddress { get; }
        public string Location { get; }
        public bool IsOnline { get; }
        public DateTime? LastSeenUtc { get; }

        public string Display => string.IsNullOrWhiteSpace(Name)
            ? $"{SerialNumber} ({IpAddress}) - {(IsOnline ? "Online" : "Offline")}"
            : $"{Name} ({IpAddress}) - {(IsOnline ? "Online" : "Offline")}";
    }

    private sealed class AttendanceRow
    {
        public AttendanceRow(AttendanceRecord record, Employee? employee, Device? device)
        {
            EmployeeCode = employee?.EmployeeCode ?? (string.IsNullOrWhiteSpace(record.EmployeeCode) ? "Unknown" : record.EmployeeCode);
            EmployeeName = employee?.FullName ?? (string.IsNullOrWhiteSpace(record.EmployeeCode) ? "Unknown" : record.EmployeeCode);
            Timestamp = record.Timestamp;
            Status = record.Status.ToString();
            DeviceName = device?.Name ?? record.DeviceSerialNumber ?? "-";
            StatusCode = record.StatusCode;
        }

        public string EmployeeCode { get; }
        public string EmployeeName { get; }
        public DateTime Timestamp { get; }
        public string Status { get; }
        public string DeviceName { get; }
        public int? StatusCode { get; }
    }

    private void StartPushServer(int port)
    {
        try
        {
            if (!_serverEventsWired)
            {
                _server.DeviceTouched += _ => SafeInvoke(() => ReloadDevices());
                _server.AttendanceReceived += _ => SafeInvoke(() =>
                {
                    ReloadEmployees();
                    ReloadAttendance();
                });
                _server.Log += message => SafeInvoke(() => _serverStatusLabel.Text = message);
                _server.RequestReceived += message => SafeInvoke(() => UpdateRequestIndicators(message));
                _serverEventsWired = true;
            }

            _server.Start(port);
            _requestHitCount = 0;
            _requestHitsLabel.Text = "Hits: 0";
            _lastRequestLabel.Text = "Last: -";
            _serverStatusLabel.Text = _server.ListeningDescription;
            _startServerButton.Enabled = false;
            _stopServerButton.Enabled = true;
            ConfigureAutoRefresh();

            if (_server.IsLocalhostOnly)
            {
                MessageBox.Show(
                    $"Server started in localhost-only mode on port {port}. Devices cannot connect from LAN (for example 192.168.100.18).\n\nRun the app as Administrator, or reserve URL ACL:\nnetsh http add urlacl url=http://+:{port}/ user=Everyone",
                    "E-Attendance",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _serverStatusLabel.Text = "Server: stopped";
            MessageBox.Show(
                $"Failed to start ZKTeco push server on port {port}.\\n{ex.Message}\\n\\nTry a different port, or run the app as Administrator, or reserve the URL ACL for this port.",
                "E-Attendance",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void StopPushServer()
    {
        _server.Stop();
        _serverStatusLabel.Text = "Server: stopped";
        _startServerButton.Enabled = true;
        _stopServerButton.Enabled = false;
        _refreshTimer.Stop();
    }

    private void StartServerFromUi()
    {
        if (!int.TryParse(_serverPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Please enter a valid port number (1-65535).", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        StopPushServer();
        StartPushServer(port);
    }

    private void ConfigureAutoRefresh()
    {
        _refreshTimer.Interval = 30000;
        _refreshTimer.Tick += (_, _) => ReloadDevices(_deviceSearchBox.Text);
        _refreshTimer.Start();
    }

    private async Task TestServerPingAsync()
    {
        if (!int.TryParse(_serverPortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Please enter a valid port number (1-65535).", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var url = $"http://127.0.0.1:{port}/iclock/ping?SN=PBTEST";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var reply = await client.GetStringAsync(url).ConfigureAwait(false);
            SafeInvoke(() => _lastRequestLabel.Text = $"Last: local ping OK ({reply.Trim()}) {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            SafeInvoke(() => _lastRequestLabel.Text = $"Last: local ping failed ({ex.Message}) {DateTime.Now:HH:mm:ss}");
        }
    }

    private void UpdateRequestIndicators(string message)
    {
        _requestHitCount++;
        _requestHitsLabel.Text = $"Hits: {_requestHitCount}";
        _lastRequestLabel.Text = $"Last: {message}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _server.Stop();
        base.OnFormClosed(e);
    }

    private void SafeInvoke(Action action)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
