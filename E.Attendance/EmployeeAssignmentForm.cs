namespace E.Attendance;

public sealed class EmployeeAssignmentForm : Form
{
    private readonly bool _includeIdentityFields;
    private readonly Dictionary<string, List<string>> _departmentsByArea;

    private readonly TextBox _employeeCodeBox = new();
    private readonly TextBox _fullNameBox = new();
    private readonly ComboBox _areaBox = new();
    private readonly CheckedListBox _departmentList = new();
    private readonly TextBox _newDepartmentBox = new();
    private readonly ComboBox _primaryDepartmentBox = new();

    private string _loadedArea = string.Empty;
    private string _preferredPrimaryDepartment = string.Empty;

    public EmployeeAssignmentForm(
        string title,
        bool includeIdentityFields,
        int? employeeCount,
        IReadOnlyList<string> availableAreas,
        Dictionary<string, List<string>> departmentsByArea,
        string employeeCode = "",
        string fullName = "",
        string area = "",
        IEnumerable<string>? selectedDepartments = null,
        string? primaryDepartment = null)
    {
        _includeIdentityFields = includeIdentityFields;
        _departmentsByArea = CloneDepartmentsByArea(departmentsByArea);

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(560, includeIdentityFields ? 470 : 410);

        _employeeCodeBox.Width = 260;
        _fullNameBox.Width = 260;

        _areaBox.Width = 260;
        _areaBox.DropDownStyle = ComboBoxStyle.DropDown;
        _areaBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _areaBox.AutoCompleteSource = AutoCompleteSource.ListItems;
        _areaBox.SelectionChangeCommitted += (_, _) => RefreshDepartmentsForArea();
        _areaBox.Leave += (_, _) => RefreshDepartmentsForArea();

        _departmentList.CheckOnClick = true;
        _departmentList.Height = 150;
        _departmentList.Width = 260;
        _departmentList.IntegralHeight = false;
        _departmentList.ItemCheck += (_, _) => BeginInvoke(UpdatePrimaryDepartmentChoices);

        _newDepartmentBox.Width = 180;

        var addDepartmentButton = new Button
        {
            Text = "Add Department",
            AutoSize = true
        };
        addDepartmentButton.Click += (_, _) => AddDepartmentForCurrentArea();

        _primaryDepartmentBox.Width = 260;
        _primaryDepartmentBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _primaryDepartmentBox.SelectedIndexChanged += (_, _) =>
        {
            _preferredPrimaryDepartment = NormalizeValue(_primaryDepartmentBox.Text);
        };

        var saveButton = new Button
        {
            Text = includeIdentityFields ? "Save Employee" : "Transfer",
            AutoSize = true
        };
        saveButton.Click += (_, _) => SaveAndClose();

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            DialogResult = DialogResult.Cancel
        };

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(14),
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var rowIndex = 0;

        if (employeeCount.HasValue)
        {
            AddRow(
                layout,
                rowIndex++,
                string.Empty,
                new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(360, 0),
                    Text = $"Selected employees: {employeeCount.Value}. Choose the Area and Department assignments to apply."
                });
        }

        if (_includeIdentityFields)
        {
            AddRow(layout, rowIndex++, "Employee Code", _employeeCodeBox);
            AddRow(layout, rowIndex++, "Full Name", _fullNameBox);
        }

        AddRow(layout, rowIndex++, "Area", _areaBox);

        var departmentInfo = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Text = "Departments are filtered by the selected area. Check multiple departments for the same employee or transfer."
        };
        AddRow(layout, rowIndex++, string.Empty, departmentInfo);
        AddRow(layout, rowIndex++, "Departments", _departmentList);

        var newDepartmentPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        newDepartmentPanel.Controls.Add(_newDepartmentBox);
        newDepartmentPanel.Controls.Add(addDepartmentButton);
        AddRow(layout, rowIndex++, "Add Dept", newDepartmentPanel);

        AddRow(layout, rowIndex++, "Primary Dept", _primaryDepartmentBox);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(saveButton);
        AddRow(layout, rowIndex++, string.Empty, buttonPanel);

        Controls.Add(layout);

        _employeeCodeBox.Text = employeeCode.Trim();
        _fullNameBox.Text = fullName.Trim();
        _preferredPrimaryDepartment = NormalizeValue(primaryDepartment);

        MergeAreas(availableAreas);
        if (!string.IsNullOrWhiteSpace(area))
        {
            EnsureAreaExists(area);
        }

        _areaBox.Text = NormalizeValue(area);
        RefreshAreaChoices();
        RefreshDepartmentsForArea(NormalizeValue(area), selectedDepartments);

        if (_includeIdentityFields)
        {
            ActiveControl = _employeeCodeBox;
        }
        else
        {
            ActiveControl = _areaBox;
        }
    }

    public string EmployeeCode => _employeeCodeBox.Text.Trim();

    public string FullName => _fullNameBox.Text.Trim();

    public string Area => _areaBox.Text.Trim();

    public List<string> SelectedDepartments => GetCheckedDepartments();

    public string PrimaryDepartment
    {
        get
        {
            var primary = NormalizeValue(_primaryDepartmentBox.Text);
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary;
            }

            return SelectedDepartments.FirstOrDefault() ?? string.Empty;
        }
    }

    private void SaveAndClose()
    {
        if (_includeIdentityFields && string.IsNullOrWhiteSpace(EmployeeCode))
        {
            MessageBox.Show("Employee code is required.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _employeeCodeBox.Focus();
            return;
        }

        if (_includeIdentityFields && string.IsNullOrWhiteSpace(FullName))
        {
            MessageBox.Show("Full name is required.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _fullNameBox.Focus();
            return;
        }

        var area = NormalizeValue(_areaBox.Text);
        if (string.IsNullOrWhiteSpace(area))
        {
            MessageBox.Show("Area is required.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _areaBox.Focus();
            return;
        }

        if (!string.Equals(area, _loadedArea, StringComparison.OrdinalIgnoreCase))
        {
            RefreshDepartmentsForArea(area, []);
        }

        if (SelectedDepartments.Count == 0)
        {
            MessageBox.Show("Select at least one department for the selected area.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _departmentList.Focus();
            return;
        }

        EnsureAreaExists(area);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddDepartmentForCurrentArea()
    {
        var area = NormalizeValue(_areaBox.Text);
        if (string.IsNullOrWhiteSpace(area))
        {
            MessageBox.Show("Enter or select an area before adding a department.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _areaBox.Focus();
            return;
        }

        var department = NormalizeValue(_newDepartmentBox.Text);
        if (string.IsNullOrWhiteSpace(department))
        {
            MessageBox.Show("Department name is required.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _newDepartmentBox.Focus();
            return;
        }

        EnsureAreaExists(area);
        var departments = _departmentsByArea[area];
        if (!departments.Any(existing => string.Equals(existing, department, StringComparison.OrdinalIgnoreCase)))
        {
            departments.Add(department);
            departments.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var selectedDepartments = GetCheckedDepartments();
        selectedDepartments.Add(department);
        _areaBox.Text = area;
        _newDepartmentBox.Clear();
        RefreshDepartmentsForArea(area, selectedDepartments);
        _newDepartmentBox.Focus();
    }

    private void RefreshDepartmentsForArea()
        => RefreshDepartmentsForArea(_areaBox.Text, null);

    private void RefreshDepartmentsForArea(string? area, IEnumerable<string>? selectedDepartments)
    {
        var normalizedArea = NormalizeValue(area);
        if (!string.IsNullOrWhiteSpace(normalizedArea))
        {
            EnsureAreaExists(normalizedArea);
        }

        _loadedArea = normalizedArea;

        var departmentLookup = string.IsNullOrWhiteSpace(normalizedArea) || !_departmentsByArea.TryGetValue(normalizedArea, out var items)
            ? []
            : items;

        var selected = new HashSet<string>(
            (selectedDepartments ?? GetCheckedDepartments())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim()),
            StringComparer.OrdinalIgnoreCase);

        _departmentList.BeginUpdate();
        try
        {
            _departmentList.Items.Clear();
            foreach (var department in departmentLookup)
            {
                var index = _departmentList.Items.Add(department);
                if (selected.Contains(department))
                {
                    _departmentList.SetItemChecked(index, true);
                }
            }
        }
        finally
        {
            _departmentList.EndUpdate();
        }

        UpdatePrimaryDepartmentChoices();
    }

    private void UpdatePrimaryDepartmentChoices()
    {
        var checkedDepartments = GetCheckedDepartments();
        var preferred = NormalizeValue(_preferredPrimaryDepartment);
        if (string.IsNullOrWhiteSpace(preferred))
        {
            preferred = NormalizeValue(_primaryDepartmentBox.Text);
        }

        _primaryDepartmentBox.BeginUpdate();
        try
        {
            _primaryDepartmentBox.Items.Clear();
            foreach (var department in checkedDepartments)
            {
                _primaryDepartmentBox.Items.Add(department);
            }
        }
        finally
        {
            _primaryDepartmentBox.EndUpdate();
        }

        if (checkedDepartments.Count == 0)
        {
            _primaryDepartmentBox.SelectedIndex = -1;
            _primaryDepartmentBox.Text = string.Empty;
            _preferredPrimaryDepartment = string.Empty;
            return;
        }

        var primary = checkedDepartments.FirstOrDefault(department =>
                string.Equals(department, preferred, StringComparison.OrdinalIgnoreCase))
            ?? checkedDepartments[0];

        _primaryDepartmentBox.SelectedItem = primary;
        _preferredPrimaryDepartment = primary;
    }

    private void MergeAreas(IEnumerable<string> areas)
    {
        foreach (var area in areas)
        {
            EnsureAreaExists(area);
        }
    }

    private void EnsureAreaExists(string? area)
    {
        var normalized = NormalizeValue(area);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!_departmentsByArea.ContainsKey(normalized))
        {
            _departmentsByArea[normalized] = [];
        }

        RefreshAreaChoices();
    }

    private void RefreshAreaChoices()
    {
        var currentText = _areaBox.Text;
        var currentSelectionStart = _areaBox.SelectionStart;
        var currentSelectionLength = _areaBox.SelectionLength;

        var areas = _departmentsByArea.Keys
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _areaBox.BeginUpdate();
        try
        {
            _areaBox.Items.Clear();
            _areaBox.Items.AddRange(areas);
        }
        finally
        {
            _areaBox.EndUpdate();
        }

        _areaBox.Text = currentText;
        if (currentSelectionStart >= 0 && currentSelectionStart <= _areaBox.Text.Length)
        {
            _areaBox.SelectionStart = currentSelectionStart;
            _areaBox.SelectionLength = Math.Min(currentSelectionLength, _areaBox.Text.Length - currentSelectionStart);
        }
    }

    private List<string> GetCheckedDepartments()
    {
        return _departmentList.CheckedItems
            .Cast<object>()
            .Select(static item => item?.ToString()?.Trim() ?? string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, List<string>> CloneDepartmentsByArea(Dictionary<string, List<string>> source)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (area, departments) in source)
        {
            var normalizedArea = NormalizeValue(area);
            var normalizedDepartments = (departments ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            clone[normalizedArea] = normalizedDepartments;
        }

        return clone;
    }

    private static string NormalizeValue(string? value)
        => value?.Trim() ?? string.Empty;

    private static void AddRow(TableLayoutPanel layout, int rowIndex, string labelText, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(
            new Label
            {
                AutoSize = true,
                Text = labelText,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 7, 10, 0)
            },
            0,
            rowIndex);

        control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        control.Margin = new Padding(0, 4, 0, 4);
        layout.Controls.Add(control, 1, rowIndex);
    }
}
