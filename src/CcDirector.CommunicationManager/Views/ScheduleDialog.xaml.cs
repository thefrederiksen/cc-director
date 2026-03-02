using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;

namespace CommunicationManager.Views;

public partial class ScheduleDialog : Window
{
    public string SelectedTiming { get; private set; } = "asap";
    public DateTime? SelectedDateTime { get; private set; }

    public ScheduleDialog(DateTime? currentScheduledFor = null)
    {
        FileLog.Write("[ScheduleDialog] Constructor");
        InitializeComponent();

        var tomorrow = DateTime.Today.AddDays(1);
        TomorrowMorningLabel.Text = $"({tomorrow:ddd, MMM d} 9:00 AM)";
        TomorrowAfternoonLabel.Text = $"({tomorrow:ddd, MMM d} 2:00 PM)";

        ScheduleDatePicker.SelectedDate = currentScheduledFor?.Date ?? tomorrow;

        if (currentScheduledFor.HasValue)
        {
            PreFillFromExisting(currentScheduledFor.Value);
        }
    }

    private void PreFillFromExisting(DateTime existing)
    {
        FileLog.Write($"[ScheduleDialog] PreFillFromExisting: {existing}");
        ScheduleOption.IsChecked = true;
        ScheduleDatePicker.SelectedDate = existing.Date;

        var hour = existing.Hour;
        var isPm = hour >= 12;
        if (hour > 12) hour -= 12;
        if (hour == 0) hour = 12;

        HourBox.Text = hour.ToString();
        MinuteBox.Text = existing.Minute.ToString("D2");
        AmPmBox.SelectedIndex = isPm ? 1 : 0;

        UpdateScheduleSummary();
    }

    private void TimingOption_Checked(object sender, RoutedEventArgs e)
    {
        if (DateTimePanel == null) return;

        DateTimePanel.Visibility = ScheduleOption?.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (ConfirmButton == null) return;

        if (HoldOption?.IsChecked == true)
        {
            ConfirmButton.Content = "Approve (Hold)";
        }
        else if (ScheduleOption?.IsChecked == true)
        {
            ConfirmButton.Content = "Approve & Schedule";
        }
        else
        {
            ConfirmButton.Content = "Approve";
        }
    }

    private void TimePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var time = button.Tag?.ToString();
        if (string.IsNullOrEmpty(time)) return;

        FileLog.Write($"[ScheduleDialog] TimePreset_Click: {time}");

        var parts = time.Split(':');
        if (parts.Length != 2) return;

        var hour = int.Parse(parts[0]);
        var minute = int.Parse(parts[1]);

        var isPm = hour >= 12;
        if (hour > 12) hour -= 12;
        if (hour == 0) hour = 12;

        HourBox.Text = hour.ToString();
        MinuteBox.Text = minute.ToString("D2");
        AmPmBox.SelectedIndex = isPm ? 1 : 0;

        UpdateScheduleSummary();
    }

    private void TimeInput_Changed(object sender, EventArgs e)
    {
        UpdateScheduleSummary();
    }

    private void UpdateScheduleSummary()
    {
        if (ScheduleSummary == null || ScheduleDatePicker == null) return;

        var dt = BuildScheduledDateTime();
        if (dt.HasValue)
        {
            ScheduleSummary.Text = $"Will send: {dt:ddd, MMM d, yyyy 'at' h:mm tt}";
        }
        else
        {
            ScheduleSummary.Text = "Select a valid date and time";
        }
    }

    private DateTime? BuildScheduledDateTime()
    {
        if (ScheduleDatePicker.SelectedDate == null) return null;

        if (!int.TryParse(HourBox.Text, out var hour) || hour < 1 || hour > 12) return null;
        if (!int.TryParse(MinuteBox.Text, out var minute) || minute < 0 || minute > 59) return null;

        var isPm = AmPmBox.SelectedIndex == 1;
        if (isPm && hour != 12) hour += 12;
        if (!isPm && hour == 12) hour = 0;

        return ScheduleDatePicker.SelectedDate.Value.Date.AddHours(hour).AddMinutes(minute);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ScheduleDialog] Confirm_Click");

        if (AsapOption.IsChecked == true)
        {
            SelectedTiming = "asap";
            SelectedDateTime = null;
        }
        else if (TomorrowMorningOption.IsChecked == true)
        {
            SelectedTiming = "scheduled";
            SelectedDateTime = DateTime.Today.AddDays(1).AddHours(9);
        }
        else if (TomorrowAfternoonOption.IsChecked == true)
        {
            SelectedTiming = "scheduled";
            SelectedDateTime = DateTime.Today.AddDays(1).AddHours(14);
        }
        else if (ScheduleOption.IsChecked == true)
        {
            var dt = BuildScheduledDateTime();
            if (dt == null)
            {
                MessageBox.Show(this, "Please select a valid date and time.", "Invalid Schedule",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SelectedTiming = "scheduled";
            SelectedDateTime = dt;
        }
        else if (HoldOption.IsChecked == true)
        {
            SelectedTiming = "hold";
            SelectedDateTime = null;
        }

        FileLog.Write($"[ScheduleDialog] Result: timing={SelectedTiming}, dateTime={SelectedDateTime}");
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[ScheduleDialog] Cancel_Click");
        DialogResult = false;
    }
}
