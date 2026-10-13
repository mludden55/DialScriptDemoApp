using System.IO;
using System.Linq;
using System.Windows;

namespace ConnectIVRTester
{
    public class CheckableBatchEntry
    {
        public string Name { get; set; } = string.Empty;
        public bool IsChecked { get; set; }
    }

    public partial class ScheduleDialog : Window
    {
        private readonly List<CheckableBatchEntry> _batchChecklistSource;

        /// <summary>The schedule the user configured, set only when the dialog closes via Save.</summary>
        public ScheduledBatchRun? Result { get; private set; }

        public ScheduleDialog(List<string> availableBatchNames)
        {
            InitializeComponent();

            _batchChecklistSource = availableBatchNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new CheckableBatchEntry { Name = n })
                .ToList();
            BatchChecklist.ItemsSource = _batchChecklistSource;

            for (int totalMinutes = 0; totalMinutes < 24 * 60; totalMinutes += 15)
            {
                var display = DateTime.Today.AddMinutes(totalMinutes).ToString("h:mm tt");
                TimeOfDayBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = display, Tag = totalMinutes });
            }
            TimeOfDayBox.SelectedIndex = 36; // defaults to 9:00 AM

            DatePickerControl.SelectedDate = DateTime.Today;

            Loaded += (_, _) => NameBox.Focus();
        }

        private void ScheduleType_Changed(object sender, RoutedEventArgs e)
        {
            if (DatePickerControl == null || DayOfWeekBox == null) return; // fires during InitializeComponent too

            DatePickerControl.Visibility = SingleRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DayOfWeekBox.Visibility = WeeklyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChooseLog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Choose log file for this schedule",
                FileName = string.IsNullOrWhiteSpace(NameBox.Text)
                    ? "schedule-log.txt"
                    : $"{SanitizeFileName(NameBox.Text)}-log.txt",
                InitialDirectory = AppPaths.LogsDir(EnvironmentContext.Current),
                Filter = "Log/Text files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
                OverwritePrompt = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                LogLocationBox.Text = dialog.FileName;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show(this, "Enter a name for this schedule.", "Name required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var chosenBatches = _batchChecklistSource.Where(b => b.IsChecked).Select(b => b.Name).ToList();
            if (chosenBatches.Count == 0)
            {
                System.Windows.MessageBox.Show(this, "Select at least one batch to run.", "Batch required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(LogLocationBox.Text))
            {
                System.Windows.MessageBox.Show(this, "Choose a log file location - this schedule runs unattended, " +
                    "so it needs somewhere to record what happened.", "Log location required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var type = SingleRadio.IsChecked == true ? ScheduleType.Single
                     : DailyRadio.IsChecked == true ? ScheduleType.Daily
                     : ScheduleType.Weekly;

            if (type == ScheduleType.Single && DatePickerControl.SelectedDate == null)
            {
                System.Windows.MessageBox.Show(this, "Choose a date for this one-time schedule.", "Date required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (type == ScheduleType.Weekly && DayOfWeekBox.SelectedItem == null)
            {
                System.Windows.MessageBox.Show(this, "Choose a day of the week.", "Day required",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var timeMinutes = (int)((System.Windows.Controls.ComboBoxItem)TimeOfDayBox.SelectedItem).Tag;

            var schedule = new ScheduledBatchRun
            {
                Name = name,
                BatchNames = chosenBatches,
                Type = type,
                TimeOfDayMinutes = timeMinutes,
                Enabled = EnabledCheckBox.IsChecked == true,
                LogFilePath = LogLocationBox.Text
            };

            if (type == ScheduleType.Single)
            {
                var date = DatePickerControl.SelectedDate!.Value.Date;
                var singleDateTime = date.AddMinutes(timeMinutes);
                if (singleDateTime <= DateTime.Now)
                {
                    System.Windows.MessageBox.Show(this,
                        "That date and time have already passed. Choose a future date/time for a one-time schedule.",
                        "Time already passed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                schedule.SingleDateTime = singleDateTime;
            }
            else if (type == ScheduleType.Weekly)
            {
                var dayName = ((System.Windows.Controls.ComboBoxItem)DayOfWeekBox.SelectedItem).Content.ToString()!;
                schedule.DayOfWeek = Enum.Parse<DayOfWeek>(dayName);
            }

            Result = schedule;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
