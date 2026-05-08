using System.ComponentModel;

namespace E.Attendance;

public sealed class CalendarDatePicker : UserControl
{
    private readonly Label _valueLabel = new();
    private readonly Button _openButton = new();
    private DateTime _selectedDate = DateTime.Today;
    private CalendarType _calendarType = CalendarType.AD;

    public event EventHandler? SelectedDateChanged;
    public event EventHandler? CalendarTypeChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Func<Dictionary<DateOnly, string>>? HolidayProvider { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime SelectedDate
    {
        get => _selectedDate;
        set
        {
            _selectedDate = value.Date;
            RefreshDisplay();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CalendarType CalendarType
    {
        get => _calendarType;
        set
        {
            if (_calendarType == value)
            {
                return;
            }

            _calendarType = value;
            RefreshDisplay();
            CalendarTypeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public CalendarDatePicker()
    {
        Height = 30;
        Width = 210;
        BackColor = Color.White;

        _valueLabel.Dock = DockStyle.Fill;
        _valueLabel.TextAlign = ContentAlignment.MiddleLeft;
        _valueLabel.Padding = new Padding(8, 0, 4, 0);
        _valueLabel.ForeColor = Color.FromArgb(28, 44, 80);
        _valueLabel.Font = new Font("Segoe UI", 9f, FontStyle.Regular);

        _openButton.Dock = DockStyle.Right;
        _openButton.Width = 30;
        _openButton.Text = "▼";
        _openButton.FlatStyle = FlatStyle.Flat;
        _openButton.FlatAppearance.BorderColor = Color.FromArgb(191, 207, 238);
        _openButton.BackColor = Color.FromArgb(235, 241, 254);
        _openButton.Click += (_, _) => OpenCalendar();

        Controls.Add(_valueLabel);
        Controls.Add(_openButton);
        BorderStyle = BorderStyle.FixedSingle;

        RefreshDisplay();
    }

    private void OpenCalendar()
    {
        using var popup = new CalendarPopupForm(SelectedDate, CalendarType, HolidayProvider?.Invoke());
        popup.StartPosition = FormStartPosition.Manual;
        popup.Location = ResolvePopupLocation(popup.Size);

        if (popup.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        var previousDate = SelectedDate;
        SelectedDate = popup.SelectedAdDate;
        CalendarType = popup.SelectedCalendarType;

        if (previousDate != SelectedDate)
        {
            SelectedDateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Point ResolvePopupLocation(Size popupSize)
    {
        var controlScreen = PointToScreen(Point.Empty);
        var preferred = new Point(controlScreen.X, controlScreen.Y + Height + 2);
        var workArea = Screen.FromPoint(controlScreen).WorkingArea;

        var x = Math.Max(workArea.Left, Math.Min(preferred.X, workArea.Right - popupSize.Width));
        var y = preferred.Y;

        // If there is no room below, place the popup above the control.
        if (y + popupSize.Height > workArea.Bottom)
        {
            y = controlScreen.Y - popupSize.Height - 2;
        }

        y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - popupSize.Height));
        return new Point(x, y);
    }

    private void RefreshDisplay()
    {
        string bsText;
        try
        {
            var bs = NepaliCalendarConverter.ConvertAdToBs(SelectedDate);
            bsText = $"{bs.Year:0000}-{bs.Month:00}-{bs.Day:00} BS";
        }
        catch
        {
            bsText = "BS n/a";
        }

        _valueLabel.Text = CalendarType == CalendarType.BS
            ? bsText
            : $"{SelectedDate:yyyy-MM-dd} AD";
        _valueLabel.Text += $"  |  {SelectedDate:dd MMM}";
    }
}
