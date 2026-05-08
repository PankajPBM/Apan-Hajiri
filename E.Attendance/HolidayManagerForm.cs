using System.ComponentModel;

namespace E.Attendance;

public sealed class HolidayManagerForm : Form
{
    private readonly BindingList<HolidayRow> _rows = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _nameBox = new();
    private readonly DateTimePicker _datePicker = new();
    private readonly CheckBox _activeBox = new();

    public HolidayManagerForm()
    {
        Text = "Holiday Settings";
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        BuildLayout();
        LoadRows();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var input = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(4)
        };
        _nameBox.Width = 220;
        _datePicker.Format = DateTimePickerFormat.Short;
        _datePicker.Width = 120;
        _activeBox.Text = "Active";
        _activeBox.Checked = true;
        var addButton = new Button { Text = "Add Holiday", AutoSize = true };
        var removeButton = new Button { Text = "Remove Selected", AutoSize = true };

        addButton.Click += (_, _) => AddHoliday();
        removeButton.Click += (_, _) => RemoveSelected();

        input.Controls.Add(new Label { Text = "Name", AutoSize = true, Padding = new Padding(0, 8, 6, 0) });
        input.Controls.Add(_nameBox);
        input.Controls.Add(new Label { Text = "Date (AD)", AutoSize = true, Padding = new Padding(10, 8, 6, 0) });
        input.Controls.Add(_datePicker);
        input.Controls.Add(_activeBox);
        input.Controls.Add(addButton);
        input.Controls.Add(removeButton);

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.DataSource = _rows;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => Close();
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);

        root.Controls.Add(input, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(actions, 0, 2);
        Controls.Add(root);
    }

    private void LoadRows()
    {
        _rows.Clear();
        foreach (var item in HolidayStore.Load())
        {
            _rows.Add(new HolidayRow
            {
                Name = item.Name,
                Date = item.Date.ToDateTime(TimeOnly.MinValue),
                IsActive = item.IsActive
            });
        }
    }

    private void AddHoliday()
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            MessageBox.Show("Holiday name is required.", "E-Attendance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _rows.Add(new HolidayRow
        {
            Name = _nameBox.Text.Trim(),
            Date = _datePicker.Value.Date,
            IsActive = _activeBox.Checked
        });

        _nameBox.Clear();
    }

    private void RemoveSelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is HolidayRow row)
        {
            _rows.Remove(row);
        }
    }

    private void SaveAndClose()
    {
        var items = _rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new HolidayItem
            {
                Name = x.Name.Trim(),
                Date = DateOnly.FromDateTime(x.Date.Date),
                IsActive = x.IsActive
            })
            .ToList();

        HolidayStore.Save(items);
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed class HolidayRow
    {
        public string Name { get; set; } = string.Empty;
        public DateTime Date { get; set; } = DateTime.Today;
        public bool IsActive { get; set; } = true;
    }
}

