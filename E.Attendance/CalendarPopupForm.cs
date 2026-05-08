using System.ComponentModel;

namespace E.Attendance;

public sealed class CalendarPopupForm : Form
{
    private static readonly string[] WeekNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] BsMonthNames = ["Baisakh", "Jestha", "Asar", "Shrawan", "Bhadra", "Aswin", "Kartik", "Mangsir", "Poush", "Magh", "Falgun", "Chaitra"];

    private readonly ComboBox _calendarTypePicker = new();
    private readonly Label _titleLabel = new();
    private readonly TableLayoutPanel _grid = new();
    private readonly Button _prevButton = new();
    private readonly Button _nextButton = new();
    private readonly Dictionary<DateOnly, string> _holidayMap;
    private DateTime _displayAd;

    public CalendarType SelectedCalendarType => (CalendarType)_calendarTypePicker.SelectedIndex;
    public DateTime SelectedAdDate { get; private set; }

    public CalendarPopupForm(DateTime initialAdDate, CalendarType type, Dictionary<DateOnly, string>? holidayMap = null)
    {
        SelectedAdDate = initialAdDate.Date;
        _displayAd = initialAdDate.Date;
        _holidayMap = holidayMap ?? [];

        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 580;
        Height = 520;
        Text = "Select Date";
        BackColor = Color.FromArgb(245, 248, 255);

        BuildLayout(type);
        RefreshCalendar();
    }

    private void BuildLayout(CalendarType type)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            RowCount = 2,
            ColumnCount = 3,
            AutoSize = true,
            BackColor = Color.FromArgb(23, 116, 220),
            Padding = new Padding(10),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _prevButton.Text = "<";
        _prevButton.Width = 38;
        _prevButton.Height = 32;
        _prevButton.FlatStyle = FlatStyle.Flat;
        _prevButton.ForeColor = Color.White;
        _prevButton.FlatAppearance.BorderColor = Color.FromArgb(130, 180, 255);
        _prevButton.BackColor = Color.FromArgb(36, 132, 236);
        _prevButton.Click += (_, _) => MoveMonth(-1);

        _nextButton.Text = ">";
        _nextButton.Width = 38;
        _nextButton.Height = 32;
        _nextButton.FlatStyle = FlatStyle.Flat;
        _nextButton.ForeColor = Color.White;
        _nextButton.FlatAppearance.BorderColor = Color.FromArgb(130, 180, 255);
        _nextButton.BackColor = Color.FromArgb(36, 132, 236);
        _nextButton.Click += (_, _) => MoveMonth(1);

        _titleLabel.ForeColor = Color.White;
        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.TextAlign = ContentAlignment.MiddleCenter;
        _titleLabel.Font = new Font("Segoe UI Semibold", 12, FontStyle.Bold);

        _calendarTypePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _calendarTypePicker.Items.AddRange(["AD", "BS"]);
        _calendarTypePicker.SelectedIndex = type == CalendarType.BS ? 1 : 0;
        _calendarTypePicker.SelectedIndexChanged += (_, _) => RefreshCalendar();

        var typeWrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 8, 0, 0)
        };
        typeWrap.Controls.Add(new Label
        {
            Text = "Calendar",
            ForeColor = Color.White,
            AutoSize = true,
            Padding = new Padding(0, 4, 6, 0)
        });
        typeWrap.Controls.Add(_calendarTypePicker);

        header.Controls.Add(_prevButton, 0, 0);
        header.Controls.Add(_titleLabel, 1, 0);
        header.Controls.Add(_nextButton, 2, 0);
        header.Controls.Add(typeWrap, 1, 1);
        header.SetColumnSpan(typeWrap, 2);

        _grid.Dock = DockStyle.Fill;
        _grid.RowCount = 7;
        _grid.ColumnCount = 7;
        _grid.BackColor = Color.FromArgb(220, 230, 248);
        _grid.Padding = new Padding(2);
        _grid.Margin = new Padding(0);
        for (var col = 0; col < 7; col++)
        {
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7f));
        }

        _grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        for (var row = 1; row < 7; row++)
        {
            _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 6f));
        }

        var action = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };
        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        ok.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.Click += (_, _) => Close();
        action.Controls.Add(ok);
        action.Controls.Add(cancel);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(action, 0, 2);
        Controls.Add(root);
    }

    private void MoveMonth(int delta)
    {
        if (SelectedCalendarType == CalendarType.AD)
        {
            _displayAd = new DateTime(_displayAd.Year, _displayAd.Month, 1).AddMonths(delta);
        }
        else
        {
            BsDate bs;
            try
            {
                bs = NepaliCalendarConverter.ConvertAdToBs(_displayAd);
            }
            catch
            {
                _calendarTypePicker.SelectedIndex = 0;
                RefreshCalendar();
                return;
            }

            var month = bs.Month + delta;
            var year = bs.Year;
            while (month < 1)
            {
                month += 12;
                year--;
            }

            while (month > 12)
            {
                month -= 12;
                year++;
            }

            try
            {
                _displayAd = NepaliCalendarConverter.ConvertBsToAd(year, month, 1);
            }
            catch
            {
                return;
            }
        }

        RefreshCalendar();
    }

    private void RefreshCalendar()
    {
        _grid.Controls.Clear();

        for (var i = 0; i < WeekNames.Length; i++)
        {
            var weekday = new Label
            {
                Text = WeekNames[i],
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = i == 6 ? Color.Firebrick : Color.FromArgb(36, 58, 95),
                BackColor = Color.FromArgb(206, 225, 252),
                Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold)
            };
            _grid.Controls.Add(weekday, i, 0);
        }

        if (SelectedCalendarType == CalendarType.AD)
        {
            PopulateAdGrid();
        }
        else
        {
            PopulateBsGrid();
        }
    }

    private void PopulateAdGrid()
    {
        var first = new DateTime(_displayAd.Year, _displayAd.Month, 1);
        _titleLabel.Text = first.ToString("MMMM yyyy");
        var start = first.AddDays(-(int)first.DayOfWeek);

        for (var i = 0; i < 42; i++)
        {
            var cellDate = start.AddDays(i).Date;
            var isCurrentMonth = cellDate.Month == first.Month && cellDate.Year == first.Year;
            var secondary = "--";
            try
            {
                var bsDate = NepaliCalendarConverter.ConvertAdToBs(cellDate);
                secondary = $"{bsDate.Day:00}";
            }
            catch
            {
                // AD month can still be shown when BS mapping is unavailable.
            }

            AddDateCell(i, cellDate, isCurrentMonth, cellDate.Day.ToString(), secondary);
        }
    }

    private void PopulateBsGrid()
    {
        BsDate displayBs;
        try
        {
            displayBs = NepaliCalendarConverter.ConvertAdToBs(_displayAd);
        }
        catch
        {
            _calendarTypePicker.SelectedIndex = 0;
            PopulateAdGrid();
            return;
        }
        _titleLabel.Text = $"{BsMonthNames[displayBs.Month - 1]} {displayBs.Year}";

        var firstAd = NepaliCalendarConverter.ConvertBsToAd(displayBs.Year, displayBs.Month, 1);
        var start = firstAd.AddDays(-(int)firstAd.DayOfWeek);

        for (var i = 0; i < 42; i++)
        {
            var cellDate = start.AddDays(i).Date;
            BsDate bsDate;
            try
            {
                bsDate = NepaliCalendarConverter.ConvertAdToBs(cellDate);
            }
            catch
            {
                continue;
            }
            var isCurrentMonth = bsDate.Month == displayBs.Month && bsDate.Year == displayBs.Year;
            AddDateCell(i, cellDate, isCurrentMonth, bsDate.Day.ToString(), cellDate.ToString("dd MMM"));
        }
    }

    private void AddDateCell(int index, DateTime cellDate, bool isCurrentMonth, string primaryText, string secondaryText)
    {
        var row = index / 7 + 1;
        var col = index % 7;
        var dateOnly = DateOnly.FromDateTime(cellDate);
        var isSaturday = cellDate.DayOfWeek == DayOfWeek.Saturday;
        var isHoliday = _holidayMap.ContainsKey(dateOnly);
        var isSelected = cellDate.Date == SelectedAdDate.Date;
        var isToday = cellDate.Date == DateTime.Today.Date;

        var cell = new CalendarCellPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
            Cursor = Cursors.Hand,
            BorderColor = isSelected ? Color.FromArgb(33, 126, 238) : Color.FromArgb(196, 214, 241),
            BorderWidth = isSelected ? 2 : 1,
            BackColor = isCurrentMonth ? Color.White : Color.FromArgb(239, 244, 252),
        };

        if (isToday && !isSelected)
        {
            cell.BackColor = Color.FromArgb(230, 255, 238);
            cell.BorderColor = Color.ForestGreen;
        }

        var textColor = (isSaturday || isHoliday) ? Color.Firebrick : (isCurrentMonth ? Color.FromArgb(19, 36, 79) : Color.Gray);
        var primary = new Label
        {
            Text = primaryText,
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = textColor,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Padding = new Padding(6, 3, 0, 0),
            BackColor = Color.Transparent
        };
        var secondary = new Label
        {
            Text = secondaryText,
            Dock = DockStyle.Top,
            Height = 18,
            TextAlign = ContentAlignment.TopRight,
            ForeColor = Color.FromArgb(75, 88, 122),
            Font = new Font("Segoe UI", 7.5f, FontStyle.Regular),
            Padding = new Padding(0, 0, 6, 0),
            BackColor = Color.Transparent
        };

        var note = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.Firebrick,
            Font = new Font("Segoe UI", 7f, FontStyle.Italic),
            Padding = new Padding(6, 0, 3, 3),
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Text = isHoliday ? _holidayMap[dateOnly] : string.Empty
        };

        void SelectDate()
        {
            SelectedAdDate = cellDate;
            _displayAd = cellDate;
            RefreshCalendar();
        }

        cell.Click += (_, _) => SelectDate();
        primary.Click += (_, _) => SelectDate();
        secondary.Click += (_, _) => SelectDate();
        note.Click += (_, _) => SelectDate();

        cell.Controls.Add(note);
        cell.Controls.Add(secondary);
        cell.Controls.Add(primary);
        _grid.Controls.Add(cell, col, row);
    }

    private sealed class CalendarCellPanel : Panel
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public Color BorderColor { get; set; } = Color.LightGray;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public int BorderWidth { get; set; } = 1;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(BorderColor, BorderWidth);
            var rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            e.Graphics.DrawRectangle(pen, rect);
        }
    }
}
