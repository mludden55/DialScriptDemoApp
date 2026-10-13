using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ConnectIVRTester
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<Step> Steps = new();
        private readonly TwilioClient _twilioClient = new();
        private int? _remainingCredits; // null until the demo service has told us
        private PhoneBook _phoneBook = new();

        private List<CheckableScriptEntry> _batchChecklistSource = new();
        private ICollectionView? _batchChecklistView;

        // Tracks the currently loaded script, if any, so Save Script can switch to
        // "Update Script" and default back to the same file instead of a new name.
        private string? _loadedScriptPath;
        private string? _loadedScriptName;

        private string? _logFilePath;
        private string? _scriptLogFilePath;       // per-script override: null = use the default/batch setting above, "" = logging off for this script
        private string? _scriptRecordingsFolder;  // per-script override, null = use the default/batch setting above
        private TranscriptionSettings _transcriptionSettings = new();
        private PlaybackSettings _playbackSettings = new();

        private bool _formattingNewPhoneNumber;
        private bool _autoFillingDescription;
        private bool _descriptionManuallyEdited;
        private bool _suppressPhoneAutocomplete;
        private bool _batchCancelRequested;
        private bool _suppressEnvironmentChange;
        private ScheduledBatchRunsData _schedules = new();
        private DispatcherTimer? _schedulerTimer;
        private bool _schedulerTickRunning;

        public MainWindow()
        {
            InitializeComponent();
            StepsList.ItemsSource = Steps;
            Steps.CollectionChanged += (_, _) => RenumberSteps();

            // Transcription on/off is remembered across restarts. Handlers are attached after
            // setting IsChecked so loading the saved value doesn't immediately re-save it.
            _transcriptionSettings = TranscriptionSettings.Load();
            TranscribeRecordingsCheckBox.IsChecked = _transcriptionSettings.Enabled;
            TranscribeRecordingsCheckBox.Checked += TranscribeRecordingsCheckBox_Changed;
            TranscribeRecordingsCheckBox.Unchecked += TranscribeRecordingsCheckBox_Changed;

            _playbackSettings = PlaybackSettings.Load();
            PlayRecordingCheckBox.IsChecked = _playbackSettings.PlayRecordingWhenFinished;
            PlayRecordingCheckBox.Checked += PlayRecordingCheckBox_Changed;
            PlayRecordingCheckBox.Unchecked += PlayRecordingCheckBox_Changed;

            LoadEnvironments();
            SampleDataSeeder.SeedIfNeeded();
            ResetToInitialState();
            StartSchedulerTimer();
        }

        /// <summary>
        /// Restores the whole page to how it looks right after launch: hides the
        /// editing sections, clears any loaded script and all input fields, closes
        /// the Create Batch panel, clears both status logs, and reloads config,
        /// phone book, script/batch lists, and log settings fresh from disk. Used
        /// both by the constructor and by the Refresh button.
        /// </summary>
        private void ResetToInitialState()
        {
            CallTargetSectionTitle.Text = "Call Target";
            CallTargetSectionTitle.Visibility = Visibility.Collapsed;
            CallTargetGroupBox.Visibility = Visibility.Collapsed;
            StepBuilderSectionTitle.Visibility = Visibility.Collapsed;
            StepBuilderGroupBox.Visibility = Visibility.Collapsed;
            DialButton.Visibility = Visibility.Collapsed;

            _loadedScriptPath = null;
            _loadedScriptName = null;
            UpdateSaveScriptButtonLabel();

            _scriptLogFilePath = null;
            _scriptRecordingsFolder = null;
            UpdateScriptLogRecordingUi();

            Steps.Clear();
            try
            {
                _suppressPhoneAutocomplete = true;
                PhoneNumberBox.Text = string.Empty;
            }
            finally
            {
                _suppressPhoneAutocomplete = false;
            }
            PhoneSuggestionsPopup.IsOpen = false;
            SetPhoneDescription(null);

            _descriptionManuallyEdited = false;
            NewPhoneNumberBox.Text = string.Empty;
            NewPhoneDescriptionBox.Text = string.Empty;
            PhoneBookErrorText.Visibility = Visibility.Collapsed;

            ScriptSearchBox.Text = string.Empty;
            DeleteScriptButton.Visibility = Visibility.Collapsed;

            CreateBatchPanel.Visibility = Visibility.Collapsed;
            BatchScriptFilterBox.Text = string.Empty;
            BatchSearchBox.Text = string.Empty;
            BatchScriptsPreviewList.ItemsSource = null;
            DeleteBatchButton.Visibility = Visibility.Collapsed;
            ChangeBatchLocationButton.IsEnabled = false;

            StatusBox.Text = string.Empty;
            BatchStatusBox.Text = string.Empty;

            LoadConfig();
            UpdateCreditsDisplay();
            RefreshPhoneBookList();
            PopulateScriptResults(string.Empty);
            PopulateBatchResults(string.Empty);
            LoadLogSettings();
            LoadRecordingSettings();
            LoadSchedules();

            PhoneBookLocationText.Text = $"Saved locally at: {PhoneBook.FilePath}";
            ScriptRegistryLocationText.Text = $"Script names indexed at: {AppRegistry.FilePath} " +
                "(the actual script files can be saved anywhere - local drive or network - via Save Script)";
            BatchScriptSourceText.Text = $"Scripts list pulled from: {AppRegistry.FilePath}";
            BatchRegistryLocationText.Text = $"Batch names indexed at: {AppRegistry.FilePath}";
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            ResetToInitialState();
            AppendStatus("Page reset to initial state.");
        }

        // ---------- Config (config.json) - hidden from UI, loaded silently ----------

        private void LoadConfig()
        {
            if (DemoConfig.ApiUrlLooksUnset)
            {
                AppendStatus("This build has not been pointed at the demo service yet (DemoConfig.DefaultApiBaseUrl).");
                return;
            }
            if (!DemoConfig.HasKey)
            {
                AppendStatus("Demo key not entered yet - you'll be asked for it before your first call.");
                return;
            }
            _ = RefreshUsageAsync();
        }

        /// <summary>Asks the demo service how many runs this key has left.</summary>
        private async Task RefreshUsageAsync()
        {
            var usage = await _twilioClient.GetUsageAsync();
            if (usage.Success)
            {
                _remainingCredits = usage.Remaining;
                UpdateCreditsDisplay();
            }
            else if (usage.InvalidKey)
            {
                DemoConfig.ClearKey();
                _remainingCredits = null;
                UpdateCreditsDisplay();
                AppendStatus("The saved demo key isn't valid. You'll be asked for a new one before your next call.");
            }
        }

        /// <summary>Makes sure a demo key is stored, asking for one if not. False if the user cancelled.</summary>
        private bool EnsureDemoKey(string? message = null)
        {
            if (DemoConfig.ApiUrlLooksUnset)
            {
                AppendStatus("This build has not been pointed at the demo service yet, so it can't place calls.");
                return false;
            }
            if (DemoConfig.HasKey) return true;

            var dialog = new DemoKeyDialog(message) { Owner = this };
            if (dialog.ShowDialog() != true) return false;
            DemoConfig.SaveKey(dialog.EnteredKey);
            _ = RefreshUsageAsync();
            return true;
        }

        /// <summary>Handles a refused call: bad key -> forget it; limit reached -> tell the person.</summary>
        private void HandleRefusedCall(TwilioCallResult result, Action<string> log)
        {
            if (result.InvalidKey)
            {
                DemoConfig.ClearKey();
                _remainingCredits = null;
                UpdateCreditsDisplay();
                log("That demo key isn't valid. You'll be asked for a new one next time.");
            }
            else if (result.LimitReached)
            {
                _remainingCredits = 0;
                UpdateCreditsDisplay();
                System.Windows.MessageBox.Show(this, UsageTracker.LimitMessage, "Testing Limit Reached",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>Updates the red "N credits remaining" banner (hidden until known).</summary>
        private void UpdateCreditsDisplay()
        {
            if (_remainingCredits is not int remaining)
            {
                CreditsRemainingText.Visibility = Visibility.Collapsed;
                return;
            }

            CreditsRemainingText.Visibility = Visibility.Visible;
            CreditsRemainingText.Text = remaining == 1 ? "1 credit remaining" : $"{remaining} credits remaining";
        }

        // ---------- Environment ----------

        private void LoadEnvironments()
        {
            var settings = EnvironmentManager.Load();
            EnvironmentContext.Current = settings.CurrentEnvironment;
            RefreshEnvironmentComboBox(settings);
        }

        private void RefreshEnvironmentComboBox(EnvironmentSettings settings)
        {
            _suppressEnvironmentChange = true;
            EnvironmentComboBox.ItemsSource = settings.Environments;
            EnvironmentComboBox.SelectedItem = settings.CurrentEnvironment;
            _suppressEnvironmentChange = false;
        }

        private void EnvironmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEnvironmentChange) return;
            if (EnvironmentComboBox.SelectedItem is not string env) return;

            EnvironmentContext.Current = env;

            var settings = EnvironmentManager.Load();
            settings.CurrentEnvironment = env;
            EnvironmentManager.Save(settings);

            AppendStatus($"Switched to \"{env}\" environment.");
            ResetToInitialState();
        }

        private void AddEnvironment_Click(object sender, RoutedEventArgs e)
        {
            var settings = EnvironmentManager.Load();
            var dialog = new NewEnvironmentDialog(settings.Environments) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.ResultName == null) return;

            settings.Environments.Add(dialog.ResultName);
            settings.CurrentEnvironment = dialog.ResultName; // switch to it immediately
            EnvironmentManager.Save(settings);

            EnvironmentContext.Current = dialog.ResultName;
            RefreshEnvironmentComboBox(settings);
            AppendStatus($"Added and switched to new environment \"{dialog.ResultName}\".");
            ResetToInitialState();
        }

        // ---------- Batch Scheduler ----------

        private void LoadSchedules()
        {
            _schedules = ScheduledBatchRunsManager.Load();
            ScheduleList.ItemsSource = null;
            ScheduleList.ItemsSource = _schedules.Runs;
            NoSchedulesText.Visibility = _schedules.Runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddSchedule_Click(object sender, RoutedEventArgs e)
        {
            var availableBatches = AppRegistry.Load().Batches.Select(b => b.Name).ToList();
            if (availableBatches.Count == 0)
            {
                System.Windows.MessageBox.Show(this,
                    "There are no saved batches in this environment yet - create one first under Create Batch.",
                    "No batches available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new ScheduleDialog(availableBatches) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result == null) return;

            // A Daily/Weekly time that's already passed today waits for its next occurrence -
            // creating a schedule never makes it run immediately.
            if (dialog.Result.IsRecurring)
                dialog.Result.NextRunAt = dialog.Result.NextOccurrenceAfter(DateTime.Now);

            _schedules.Runs.Add(dialog.Result);
            ScheduledBatchRunsManager.Save(_schedules);
            LoadSchedules();
            AppendBatchStatus($"Added schedule \"{dialog.Result.Name}\" ({dialog.Result.ScheduleDescription}). {dialog.Result.NextRunText}");
        }

        private void ToggleScheduleEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (((System.Windows.Controls.Button)sender).Tag is not ScheduledBatchRun schedule) return;

            if (!schedule.Enabled && schedule.Type == ScheduleType.Single &&
                (schedule.LastRunAt != null || (schedule.SingleDateTime.HasValue && schedule.SingleDateTime.Value <= DateTime.Now)))
            {
                System.Windows.MessageBox.Show(this,
                    $"The time for one-time schedule \"{schedule.Name}\" has already passed, so enabling it wouldn't run anything " +
                    "(passed times are skipped, never run late). Add a new schedule with a future date and time instead.",
                    "Time already passed", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            schedule.Enabled = !schedule.Enabled;
            // Re-enabling starts fresh from now: occurrences that passed while it was
            // disabled are skipped, not run late.
            if (schedule.Enabled && schedule.IsRecurring)
                schedule.NextRunAt = schedule.NextOccurrenceAfter(DateTime.Now);
            ScheduledBatchRunsManager.Save(_schedules);
            LoadSchedules();
            AppendBatchStatus($"Schedule \"{schedule.Name}\" {(schedule.Enabled ? "enabled" : "disabled")}.");
        }

        private void DeleteSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (((System.Windows.Controls.Button)sender).Tag is not ScheduledBatchRun schedule) return;

            var confirm = System.Windows.MessageBox.Show(this, $"Delete schedule \"{schedule.Name}\"?",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _schedules.Runs.Remove(schedule);
            ScheduledBatchRunsManager.Save(_schedules);
            LoadSchedules();
            AppendBatchStatus($"Deleted schedule \"{schedule.Name}\".");
        }

        /// <summary>
        /// Starts the in-app scheduler timer. Only fires schedules while this app is
        /// open and running - there's no background-service component, so a schedule
        /// due while the app is closed simply doesn't run.
        /// </summary>
        private void StartSchedulerTimer()
        {
            SkipMissedScheduledRuns();
            _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _schedulerTimer.Tick += async (_, _) => await SchedulerTick();
            _schedulerTimer.Start();
        }

        /// <summary>
        /// Runs once at app start. Any run whose time passed while the app was closed is
        /// skipped (and noted in that schedule's log) rather than fired late: Daily/Weekly
        /// schedules move on to their next future occurrence, and a missed one-time schedule
        /// is disabled and marked as missed. Covers every environment, same as the scheduler.
        /// </summary>
        private void SkipMissedScheduledRuns()
        {
            var uiEnvironment = EnvironmentContext.Current;
            var now = DateTime.Now;
            try
            {
                foreach (var env in EnvironmentManager.Load().Environments)
                {
                    EnvironmentContext.Current = env;
                    var envSchedules = ScheduledBatchRunsManager.Load();
                    bool changed = false;

                    // One-time schedules whose time passed while the app was closed: skip them.
                    foreach (var schedule in envSchedules.Runs.Where(s =>
                                 s.Enabled && s.Type == ScheduleType.Single && s.LastRunAt == null &&
                                 s.SingleDateTime.HasValue && s.SingleDateTime.Value <= now))
                    {
                        LogScheduleLine(schedule,
                            $"Skipped the one-time run due {schedule.SingleDateTime!.Value:ddd MMM d h:mm tt} - the app wasn't open at that time.");
                        schedule.Enabled = false;
                        schedule.Missed = true;
                        changed = true;
                    }

                    foreach (var schedule in envSchedules.Runs.Where(s => s.Enabled && s.IsRecurring))
                    {
                        if (schedule.NextRunAt.HasValue && schedule.NextRunAt.Value > now) continue; // still upcoming

                        if (schedule.NextRunAt.HasValue)
                        {
                            LogScheduleLine(schedule,
                                $"Skipped the run due {schedule.NextRunAt.Value:ddd MMM d h:mm tt} - the app wasn't open at that time.");
                        }
                        schedule.NextRunAt = schedule.NextOccurrenceAfter(now);
                        changed = true;
                    }

                    if (changed) ScheduledBatchRunsManager.Save(envSchedules);
                }
            }
            catch
            {
                // Never block startup over schedule bookkeeping.
            }
            finally
            {
                EnvironmentContext.Current = uiEnvironment;
            }
            LoadSchedules();
        }

        /// <summary>
        /// Checks every environment's schedules, not just whichever one the UI currently
        /// displays - a Development schedule needs to fire even while you're looking at
        /// Production. PhoneBook/AppRegistry/RecordingSettings all resolve their paths from
        /// the shared EnvironmentContext.Current, so this temporarily swaps it to each
        /// environment in turn while checking/firing that environment's schedules, then
        /// restores whatever the UI actually had selected once the tick finishes.
        /// </summary>
        private async Task SchedulerTick()
        {
            if (_schedulerTickRunning) return; // a previous fire is still running a batch - don't overlap
            _schedulerTickRunning = true;
            var uiEnvironment = EnvironmentContext.Current;

            try
            {
                var now = DateTime.Now;
                var allEnvironments = EnvironmentManager.Load().Environments;

                foreach (var env in allEnvironments)
                {
                    EnvironmentContext.Current = env;

                    var envSchedules = ScheduledBatchRunsManager.Load();
                    // Snapshot which schedules are due before running any of them, since
                    // firing one can take minutes.
                    var due = envSchedules.Runs.Where(s => s.Enabled && IsDue(s, now)).ToList();
                    if (due.Count == 0) continue;

                    foreach (var schedule in due)
                    {
                        await FireScheduleAsync(schedule, env);
                        schedule.LastRunAt = DateTime.Now;
                        if (schedule.IsRecurring)
                        {
                            schedule.NextRunAt = schedule.NextOccurrenceAfter(DateTime.Now);
                        }
                        if (schedule.Type == ScheduleType.Single)
                        {
                            schedule.Enabled = false; // one-time schedules don't repeat
                        }
                    }

                    ScheduledBatchRunsManager.Save(envSchedules);
                }
            }
            finally
            {
                EnvironmentContext.Current = uiEnvironment; // restore whatever the UI actually had selected
                _schedulerTickRunning = false;
                LoadSchedules(); // in case the currently-displayed environment's own schedules changed
            }
        }

        private static bool IsDue(ScheduledBatchRun schedule, DateTime now)
        {
            switch (schedule.Type)
            {
                case ScheduleType.Single:
                    if (schedule.SingleDateTime == null) return false;
                    // Fires when the clock reaches its time while the app is open. One whose time
                    // passed while the app was closed was already disabled at startup
                    // (SkipMissedScheduledRuns), so it never runs late.
                    return schedule.LastRunAt == null && now >= schedule.SingleDateTime.Value;

                case ScheduleType.Daily:
                case ScheduleType.Weekly:
                    // NextRunAt is always a future time when set (on create, enable, app start,
                    // and after each run), so this only fires when the clock reaches it while
                    // the app is open - never for a time that had already passed.
                    return schedule.NextRunAt.HasValue && now >= schedule.NextRunAt.Value;

                default:
                    return false;
            }
        }

        private async Task FireScheduleAsync(ScheduledBatchRun schedule, string environment)
        {
            string tag = $"[Schedule: {schedule.Name} ({environment})]";
            void log(string message)
            {
                LogScheduleLine(schedule, message);   // persistent audit trail for this schedule
                AppendBatchStatus($"{tag} {message}"); // live visibility if the app happens to be open and watched
            }

            log($"=== Starting ({schedule.BatchNames.Count} batch(es)) ===");

            var registry = AppRegistry.Load();

            foreach (var batchName in schedule.BatchNames)
            {
                var entry = registry.Batches.FirstOrDefault(b => b.Name == batchName);
                if (entry == null)
                {
                    log($"Batch \"{batchName}\" no longer exists in this environment - skipping.");
                    continue;
                }

                BatchFile batch;
                try
                {
                    batch = BatchFile.Load(entry.FilePath);
                }
                catch (Exception ex)
                {
                    log($"Could not load batch \"{batchName}\": {ex.Message}");
                    continue;
                }

                if (batch.Scripts.Count == 0 || batch.Scripts.Count > UsageTracker.MaxBatchScripts)
                {
                    log($"Batch \"{batchName}\" is empty or exceeds the {UsageTracker.MaxBatchScripts}-script limit - skipping.");
                    continue;
                }

                var result = await RunBatchCoreAsync(batch, batchName, log, isCancelled: () => false);
                if (result.LimitReached)
                {
                    log("Testing limit reached - stopping this schedule's remaining batches.");
                    break;
                }
            }

            log("=== Finished ===");
        }

        /// <summary>Writes one timestamped line to a schedule's dedicated log file. Failures
        /// here (e.g. the folder became unavailable) are swallowed - an unattended run
        /// shouldn't crash over a logging problem, though the line is still lost in that case.</summary>
        private static void LogScheduleLine(ScheduledBatchRun schedule, string message)
        {
            if (string.IsNullOrWhiteSpace(schedule.LogFilePath)) return;
            try
            {
                var dir = Path.GetDirectoryName(schedule.LogFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(schedule.LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Best-effort logging for an unattended run - don't let a log write failure stop it.
            }
        }

        // ---------- Phone Book ----------

        /// <summary>Reformats the number as (XXX) XXX-XXXX while typing, and keeps the
        /// description in sync with it until the user edits the description themselves.</summary>
        private void NewPhoneNumberBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_formattingNewPhoneNumber) return;

            var digits = PhoneFormat.ExtractDigits(NewPhoneNumberBox.Text);
            var formatted = PhoneFormat.Format(digits);

            _formattingNewPhoneNumber = true;
            NewPhoneNumberBox.Text = formatted;
            NewPhoneNumberBox.CaretIndex = formatted.Length;
            _formattingNewPhoneNumber = false;

            if (!_descriptionManuallyEdited)
            {
                _autoFillingDescription = true;
                NewPhoneDescriptionBox.Text = formatted;
                NewPhoneDescriptionBox.CaretIndex = formatted.Length;
                _autoFillingDescription = false;
            }
        }

        /// <summary>Once the user types their own description, stop overwriting it as more digits are entered.</summary>
        private void NewPhoneDescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_autoFillingDescription) return;
            _descriptionManuallyEdited = true;
        }

        private void AddPhoneNumber_Click(object sender, RoutedEventArgs e)
        {
            var digits = PhoneFormat.ExtractDigits(NewPhoneNumberBox.Text);
            if (!Regex.IsMatch(digits, "^[0-9]{10}$"))
            {
                ShowPhoneBookError("Enter a valid 10-digit phone number to add to the Phone Book.");
                return;
            }

            var description = NewPhoneDescriptionBox.Text.Trim();
            if (description.Length == 0) description = PhoneFormat.Format(digits);

            if (_phoneBook.AddNumber(digits, description))
            {
                _phoneBook.Save();
                RefreshPhoneBookList();
                _descriptionManuallyEdited = false;
                NewPhoneNumberBox.Text = string.Empty; // cascades to clear the description too
                PhoneBookErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowPhoneBookError("That number is already in the list.");
            }
        }

        private void ShowPhoneBookError(string message)
        {
            PhoneBookErrorText.Text = message;
            PhoneBookErrorText.Visibility = Visibility.Visible;
            AppendStatus(message);
        }

        private void ShowSaveScriptError(string message)
        {
            SaveScriptErrorText.Text = message;
            SaveScriptErrorText.Visibility = Visibility.Visible;
            AppendStatus(message);
        }

        private void ShowSaveBatchError(string message)
        {
            SaveBatchErrorText.Text = message;
            SaveBatchErrorText.Visibility = Visibility.Visible;
            AppendBatchStatus(message);
        }

        private void RemovePhoneNumber_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string number)
            {
                _phoneBook.RemoveNumber(number);
                _phoneBook.Save();
                RefreshPhoneBookList();
            }
        }

        private void RefreshPhoneBookList()
        {
            _phoneBook = PhoneBook.Load();
            PhoneBookList.ItemsSource = null;
            PhoneBookList.ItemsSource = _phoneBook.Entries; // PhoneBook keeps this numerically sorted
        }

        // ---------- Phone number autocomplete on Call Target ----------

        private void PhoneNumberBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPhoneAutocomplete) return;

            SetPhoneDescription(null); // typing invalidates whatever description was shown before

            var text = PhoneNumberBox.Text.Trim();
            if (text.Length == 0)
            {
                PhoneSuggestionsPopup.IsOpen = false;
                return;
            }

            var matches = _phoneBook.SearchByNumberOrDescription(text);
            if (matches.Count > 0)
            {
                PhoneSuggestionsList.ItemsSource = matches;
                PhoneSuggestionsList.SelectedItem = null; // otherwise re-selecting the same entry as last time won't fire SelectionChanged
                PhoneSuggestionsPopup.IsOpen = true;
            }
            else
            {
                PhoneSuggestionsPopup.IsOpen = false;
            }
        }

        private void PhoneNumberBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!PhoneSuggestionsPopup.IsOpen) return;
            if (PhoneSuggestionsList.Items.Count != 1) return;

            PhoneSuggestionsList.SelectedItem = PhoneSuggestionsList.Items[0];
            e.Handled = true;
        }

        private void PhoneSuggestionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PhoneSuggestionsList.SelectedItem is PhoneBookEntry entry)
            {
                // Selecting a suggestion shouldn't re-trigger the search/reopen logic in
                // PhoneNumberBox_TextChanged - that was previously happening unguarded,
                // causing a reentrant chain (set text -> TextChanged -> re-search -> reopen
                // popup -> nested SelectionChanged...) that was fragile and inconsistent.
                try
                {
                    _suppressPhoneAutocomplete = true;
                    PhoneNumberBox.Text = entry.FormattedNumber;
                    PhoneNumberBox.CaretIndex = PhoneNumberBox.Text.Length;
                }
                finally
                {
                    _suppressPhoneAutocomplete = false;
                }
                SetPhoneDescription(entry.Description);
            }
            PhoneSuggestionsPopup.IsOpen = false;
        }

        /// <summary>Shows or hides the gray description subtitle under the Call Target phone box.</summary>
        private void SetPhoneDescription(string? description)
        {
            if (string.IsNullOrEmpty(description))
            {
                PhoneNumberDescriptionText.Text = string.Empty;
                PhoneNumberDescriptionText.Visibility = Visibility.Collapsed;
            }
            else
            {
                PhoneNumberDescriptionText.Text = description;
                PhoneNumberDescriptionText.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// The popup is StaysOpen="True" so a click inside it (selecting a suggestion)
        /// never races against an auto-close. This closes it manually when the user
        /// clicks anywhere else in the window instead.
        /// </summary>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!PhoneSuggestionsPopup.IsOpen) return;
            if (e.OriginalSource is DependencyObject clicked)
            {
                if (IsDescendantOf(clicked, PhoneSuggestionsPopup.Child) || IsDescendantOf(clicked, PhoneNumberBox))
                    return; // click was on the box itself or inside the popup - leave it open
            }
            PhoneSuggestionsPopup.IsOpen = false;
        }

        private static bool IsDescendantOf(DependencyObject? element, DependencyObject? ancestor)
        {
            while (element != null)
            {
                if (element == ancestor) return true;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        // ---------- Script Selector ----------

        private void ScriptSearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => PopulateScriptResults(ScriptSearchBox.Text);

        private void PopulateScriptResults(string query)
        {
            var registry = AppRegistry.Load();
            ScriptResultsList.ItemsSource = registry.SearchScripts(query);
        }

        private void ScriptResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => LoadSelectedScript_Click(sender, e);

        private void ScriptResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DeleteScriptButton.Visibility = ScriptResultsList.SelectedItem is RegistryEntry
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void LoadSelectedScript_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptResultsList.SelectedItem is not RegistryEntry entry)
            {
                AppendStatus("Select a script from the list first.");
                return;
            }
            LoadScriptFromPath(entry.FilePath, entry.Name);
        }

        private void LoadScriptFromPath(string path, string name)
        {
            try
            {
                var script = ScriptFile.Load(path);

                var matchingEntry = _phoneBook.Entries.FirstOrDefault(en => en.Number == script.PhoneNumber);

                try
                {
                    _suppressPhoneAutocomplete = true;
                    PhoneNumberBox.Text = PhoneFormat.Format(script.PhoneNumber ?? string.Empty);
                }
                finally
                {
                    // Must always reset, even if formatting/assignment throws - otherwise
                    // autocomplete silently stops responding for the rest of the session.
                    _suppressPhoneAutocomplete = false;
                }
                PhoneSuggestionsPopup.IsOpen = false;
                SetPhoneDescription(matchingEntry?.Description);

                _scriptLogFilePath = script.LogFilePath;
                _scriptRecordingsFolder = script.RecordingsFolder;
                UpdateScriptLogRecordingUi();

                Steps.Clear();
                foreach (var s in script.Steps)
                {
                    var type = Enum.TryParse<StepType>(s.Type, out var t) ? t : StepType.Numeric;
                    Steps.Add(new Step { Type = type, Value = s.Value, PauseSecondsBefore = s.PauseSecondsBefore > 0 ? s.PauseSecondsBefore : 3 });
                }
                if (Steps.Count == 0) AddStep();

                _loadedScriptPath = path;
                _loadedScriptName = name;
                UpdateSaveScriptButtonLabel();
                ShowScriptEditingSections(name);

                AppendStatus($"Loaded script \"{name}\".");
            }
            catch (Exception ex)
            {
                AppendStatus($"Could not load script \"{name}\": {ex.Message}");
            }
        }

        private void UpdateSaveScriptButtonLabel()
        {
            SaveScriptButton.Content = _loadedScriptPath != null ? "Update Script" : "Save Script";
            ScriptLocationDisplay.Text = _loadedScriptPath ?? "(not saved yet)";
        }

        /// <summary>
        /// Starts a brand new, empty script: clears any loaded script, resets the
        /// target/steps to blank, and reveals the Call Target / Step Builder / Dial
        /// sections (hidden until the user picks New Script or loads an existing one).
        /// </summary>
        private void NewScript_Click(object sender, RoutedEventArgs e)
        {
            _loadedScriptPath = null;
            _loadedScriptName = null;
            UpdateSaveScriptButtonLabel();

            PhoneNumberBox.Text = string.Empty;
            Steps.Clear();
            AddStep();

            _scriptLogFilePath = null;
            _scriptRecordingsFolder = null;
            UpdateScriptLogRecordingUi();

            ShowScriptEditingSections("New Script");
            AppendStatus("Started a new script.");
        }

        private void ImportScript_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a script file someone shared with you",
                Filter = "Script files (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                // Validate it's actually a readable script file before registering it.
                _ = ScriptFile.Load(dialog.FileName);
            }
            catch (Exception ex)
            {
                AppendStatus($"Could not import \"{dialog.FileName}\": {ex.Message}");
                return;
            }

            var name = Path.GetFileNameWithoutExtension(dialog.FileName);
            var registry = AppRegistry.Load();
            registry.AddOrUpdateScript(name, dialog.FileName);
            registry.Save();

            PopulateScriptResults(ScriptSearchBox.Text);
            RefreshBatchChecklistIfOpen();
            AppendStatus($"Imported \"{name}\" into the \"{EnvironmentContext.Current}\" environment's Script Selector.");
        }

        private void ShowScriptEditingSections(string header)
        {
            CallTargetSectionTitle.Text = header;
            CallTargetSectionTitle.Visibility = Visibility.Visible;
            CallTargetGroupBox.Visibility = Visibility.Visible;
            StepBuilderSectionTitle.Visibility = Visibility.Visible;
            StepBuilderGroupBox.Visibility = Visibility.Visible;
            DialButton.Visibility = Visibility.Visible;
        }

        private void DeleteScript_Click(object sender, RoutedEventArgs e)
        {
            if (ScriptResultsList.SelectedItem is not RegistryEntry entry)
            {
                AppendStatus("Select a script from the list first.");
                return;
            }

            var confirm = System.Windows.MessageBox.Show(this,
                $"Delete script \"{entry.Name}\"? This deletes the file itself:\n{entry.FilePath}",
                "Delete Script", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                if (File.Exists(entry.FilePath))
                    File.Delete(entry.FilePath);
            }
            catch (Exception ex)
            {
                AppendStatus($"Could not delete the file on disk ({ex.Message}), but removing it from the list.");
            }

            var registry = AppRegistry.Load();
            registry.Scripts.RemoveAll(s => string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            registry.Save();

            if (_loadedScriptPath != null &&
                string.Equals(Path.GetFullPath(_loadedScriptPath), Path.GetFullPath(entry.FilePath), StringComparison.OrdinalIgnoreCase))
            {
                _loadedScriptPath = null;
                _loadedScriptName = null;
                UpdateSaveScriptButtonLabel();
            }

            PopulateScriptResults(ScriptSearchBox.Text);
            RefreshBatchChecklistIfOpen();
            AppendStatus($"Deleted script \"{entry.Name}\".");
        }

        // ---------- Save Script ----------

        private void SaveScript_Click(object sender, RoutedEventArgs e)
        {
            SaveScriptErrorText.Visibility = Visibility.Collapsed;

            if (Steps.Count == 0 || Steps.Any(s => s.Type != StepType.HangUp && string.IsNullOrWhiteSpace(s.Value)))
            {
                ShowSaveScriptError("Fill in all steps before saving a script.");
                return;
            }

            string phone = PhoneFormat.ExtractPhoneDigits(PhoneNumberBox.Text);
            if (!Regex.IsMatch(phone, "^[0-9]{10}$"))
            {
                ShowSaveScriptError("Enter or select a valid 10-digit phone number before saving a script.");
                return;
            }

            bool isUpdating = _loadedScriptPath != null;

            string defaultName = isUpdating ? _loadedScriptName! : (phone.Length > 0 ? $"{phone}-" : "Script-");
            string defaultFolder = isUpdating
                ? (Path.GetDirectoryName(_loadedScriptPath!) ?? AppPaths.ScriptsDir(EnvironmentContext.Current))
                : AppPaths.ScriptsDir(EnvironmentContext.Current);

            var dialog = new SaveNameDialog(
                isUpdating ? "Update Script" : "Save Script",
                "Script name", defaultName, defaultFolder, ".json")
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.ResultPath == null) return;

            bool isDifferentFile = isUpdating && dialog.NameOrFolderChanged;

            if (isDifferentFile)
            {
                var confirm = System.Windows.MessageBox.Show(this,
                    "Saving under a different name or location will create a new script and leave the original file untouched. Continue?",
                    "Save As New Script", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.OK) return;
            }

            var script = new ScriptFile
            {
                PhoneNumber = phone,
                LogFilePath = _scriptLogFilePath,
                RecordingsFolder = _scriptRecordingsFolder,
                Steps = Steps.Select(s => new ScriptStepData { Type = s.Type.ToString(), Value = s.Value, PauseSecondsBefore = s.PauseSecondsBefore }).ToList()
            };

            try
            {
                script.Save(dialog.ResultPath);

                var name = Path.GetFileNameWithoutExtension(dialog.ResultPath);
                var registry = AppRegistry.Load();
                registry.AddOrUpdateScript(name, dialog.ResultPath);
                registry.Save();

                _loadedScriptPath = dialog.ResultPath;
                _loadedScriptName = name;
                UpdateSaveScriptButtonLabel();

                PopulateScriptResults(ScriptSearchBox.Text);
                RefreshBatchChecklistIfOpen();
                AppendStatus($"Script saved as \"{name}\" -> {dialog.ResultPath}");
            }
            catch (Exception ex)
            {
                AppendStatus($"Could not save script: {ex.Message}");
            }
        }

        // ---------- Step list management ----------

        private void AddStep_Click(object sender, RoutedEventArgs e) => AddStep();

        private void AddStep()
        {
            Steps.Add(new Step { Type = StepType.Numeric, Value = string.Empty });
        }

        private void RemoveStep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Step step)
            {
                Steps.Remove(step);
            }
        }

        private void RenumberSteps()
        {
            for (int i = 0; i < Steps.Count; i++)
                Steps[i].Number = i + 1;
        }

        // ---------- Input validation ----------

        private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
        }

        /// <summary>
        /// Call Target's phone field: letters+digits+spaces so it can be used to search
        /// saved numbers by description ("MyPhone") as well as by digits. Whatever ends
        /// up here still has to resolve to exactly 10 digits before Dial/Save will accept it.
        /// </summary>
        private void AlphaNumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, "^[a-zA-Z0-9 ]+$");
        }

        /// <summary>
        /// Enforces the two input rules from the spec:
        ///  - Numeric step: whatever Twilio's &lt;Play digits&gt; attribute actually accepts -
        ///    0-9, #, *, A-D (rare DTMF tones), and w/W to insert a 0.5s/1s pause between
        ///    tones within the step (e.g. "1w2" or "4085551212#").
        ///  - Voice step: alphanumeric characters and spaces (spaces are allowed
        ///    so voice text can form actual words/sentences for the TTS engine).
        /// </summary>
        private void StepValue_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox box || box.Tag is not Step step)
                return;

            if (step.Type == StepType.Numeric)
            {
                if (!Regex.IsMatch(e.Text, "^[0-9ABCDWw#*]+$"))
                {
                    e.Handled = true;
                }
            }
            else // Voice
            {
                if (!Regex.IsMatch(e.Text, "^[a-zA-Z0-9 ]+$"))
                {
                    e.Handled = true;
                }
            }
        }

        // ---------- Dial ----------

        private async void Dial_Click(object sender, RoutedEventArgs e)
        {
            StatusBox.Text = string.Empty;

            // If this script has its own Log Location override, use it for everything
            // logged during this run - including validation failures below - then restore
            // the default/batch setting once this Dial finishes, regardless of outcome.
            var previousLogFilePath = _logFilePath;
            if (_scriptLogFilePath != null) _logFilePath = _scriptLogFilePath;

            try
            {
                // Validate phone number
                string digits = PhoneFormat.ExtractPhoneDigits(PhoneNumberBox.Text);
                if (!Regex.IsMatch(digits, "^[0-9]{10}$"))
                {
                    AppendStatus("Enter or select a valid 10-digit phone number before dialing.");
                    return;
                }
                string toE164 = "+1" + digits; // assumes US/Canada country code

                if (!EnsureDemoKey()) return;

                // Validate steps
                if (Steps.Count == 0)
                {
                    AppendStatus("Add at least one step before dialing.");
                    return;
                }
                foreach (var step in Steps)
                {
                    if (step.Type != StepType.HangUp && string.IsNullOrWhiteSpace(step.Value))
                    {
                        AppendStatus($"Step {step.Number} is empty. Fill in every step before dialing.");
                        return;
                    }
                }
                var firstHangUp = Steps.FirstOrDefault(s => s.Type == StepType.HangUp);
                if (firstHangUp != null && firstHangUp != Steps[^1])
                {
                    AppendStatus($"Heads up: Step {firstHangUp.Number} hangs up the call, so any steps after it will never run.");
                }

                if (_remainingCredits is int left && left <= 0)
                {
                    System.Windows.MessageBox.Show(this, UsageTracker.LimitMessage, "Testing Limit Reached",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    AppendStatus(UsageTracker.LimitMessage);
                    return;
                }

                // Place the call
                DialButton.IsEnabled = false;
                AppendStatus($"Dialing {toE164}...");

                bool record = true; // recording (and cloud cleanup afterward) is always on - no longer optional
                var result = await _twilioClient.PlaceCallAsync(toE164, Steps.ToList(), record);

                if (result.Success)
                {
                    if (result.Remaining is int remainingAfter)
                    {
                        _remainingCredits = remainingAfter;
                        UpdateCreditsDisplay();
                        AppendStatus($"{remainingAfter} test(s) remaining.");
                    }

                    AppendStatus($"Call placed. Call SID: {result.CallSid}");

                    if (record && result.CallSid != null)
                    {
                        var timeout = TimeSpan.FromMinutes(5);
                        var start = DateTime.UtcNow;
                        string? lastStatus = null;
                        while (DateTime.UtcNow - start < timeout)
                        {
                            await Task.Delay(3000);
                            var statusResult = await _twilioClient.GetCallStatusAsync(result.CallSid);
                            if (statusResult.Success)
                            {
                                lastStatus = statusResult.Status;
                                if (TwilioClient.IsTerminalStatus(statusResult.Status))
                                    break;
                            }
                        }
                        AppendStatus($"Call finished (status: {lastStatus ?? "unknown/timed out"}).");

                        var savedPath = await FetchAndSaveRecordingAsync(
                            result.CallSid, "Call", AppendStatus,
                            _scriptRecordingsFolder,
                            scriptSteps: Steps.ToList());

                        if (savedPath != null && _playbackSettings.PlayRecordingWhenFinished)
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(savedPath) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                AppendStatus($"Saved the recording but couldn't open it automatically ({ex.Message}). Open it manually from: {savedPath}");
                            }
                        }
                    }
                    else
                    {
                        AppendStatus("Call placed successfully.");
                    }
                }
                else
                {
                    AppendStatus($"Call failed: {result.ErrorMessage}");
                    HandleRefusedCall(result, AppendStatus);
                }

                DialButton.IsEnabled = true;
            }
            finally
            {
                _logFilePath = previousLogFilePath;
            }
        }

        // ---------- Recordings ----------

        private string? _recordingsFolder; // null/empty = use the default below

        private static string DefaultRecordingsFolder => AppPaths.RecordingsDir(EnvironmentContext.Current);

        private string RecordingsFolder =>
            string.IsNullOrWhiteSpace(_recordingsFolder) ? DefaultRecordingsFolder : _recordingsFolder;

        /// <summary>
        /// Same as RecordingsFolder, but reads the setting fresh from disk instead of the
        /// cached _recordingsFolder field. _recordingsFolder only gets refreshed when the UI
        /// resets (environment switch/Refresh), so it can go stale if something needs the
        /// correct folder for a DIFFERENT environment than whatever's currently displayed -
        /// which is exactly what the scheduler does when it fires a non-selected environment's
        /// schedule in the background. Always call this (not the cached property) from any
        /// code path that isn't guaranteed to be acting on the currently-displayed environment.
        /// </summary>
        private static string CurrentRecordingsFolder()
        {
            var settings = RecordingSettings.Load();
            return string.IsNullOrWhiteSpace(settings.RecordingsFolder) ? DefaultRecordingsFolder : settings.RecordingsFolder;
        }

        private void LoadRecordingSettings()
        {
            var settings = RecordingSettings.Load();
            _recordingsFolder = string.IsNullOrWhiteSpace(settings.RecordingsFolder) ? null : settings.RecordingsFolder;
            UpdateRecordingLocationUi();
        }

        private void UpdateRecordingLocationUi()
        {
            RecordingLocationDisplay.Text = RecordingsFolder;
        }

        private void ChooseRecordingLocation_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose where call recordings are saved (local or network drive)",
                UseDescriptionForTitle = true,
                RootFolder = Environment.SpecialFolder.MyComputer,
                SelectedPath = Directory.Exists(RecordingsFolder) ? RecordingsFolder : DefaultRecordingsFolder
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _recordingsFolder = Path.GetFullPath(dialog.SelectedPath);
                new RecordingSettings { RecordingsFolder = _recordingsFolder }.Save();
                UpdateRecordingLocationUi();
                UpdateScriptLogRecordingUi(); // keep the per-script display accurate if it's inheriting this value
                AppendStatus($"Recordings will now be saved to: {_recordingsFolder}");
            }
        }

        /// <summary>
        /// Fetches and saves a call's recording(s) locally, so whoever ran the test can
        /// listen without needing Twilio Console access. Twilio finishes processing a
        /// recording a few seconds after the call ends, so this retries briefly rather
        /// than giving up if none are found on the first check. Returns the last saved
        /// file's path (for auto-opening a single call), or null if nothing was saved.
        ///
        /// The Twilio copy of every recording is always deleted once the local copy is
        /// verified (file exists on disk and matches the downloaded size). There's no
        /// option to keep it: the local file is what gets played back and transcribed,
        /// and leaving copies in Twilio just adds storage cost and another place test-call
        /// audio lives. If the save or the verification fails, the Twilio copy is left
        /// alone so the recording is never lost.
        /// </summary>
        private async Task<string?> FetchAndSaveRecordingAsync(
            string callSid,
            string fileLabel, Action<string> log,
            string? recordingsFolderOverride = null, IReadOnlyList<Step>? scriptSteps = null)
        {
            try
            {
                var recordingsFolder = recordingsFolderOverride ?? CurrentRecordingsFolder();
                Directory.CreateDirectory(recordingsFolder);

                List<string> sids = new();
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var listResult = await _twilioClient.GetRecordingSidsAsync(callSid);
                    if (!listResult.Success)
                    {
                        log($"Couldn't check for a recording: {listResult.ErrorMessage}");
                        return null;
                    }

                    // Only "completed" recordings actually have downloadable media - one can
                    // appear in this list while still "processing" (not yet downloadable), so
                    // finding a SID isn't enough on its own; keep waiting until it's completed.
                    var completed = listResult.Recordings.Where(r => string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)).Select(r => r.Sid).ToList();
                    if (completed.Count > 0)
                    {
                        sids = completed;
                        break;
                    }

                    var problem = listResult.Recordings.FirstOrDefault(r =>
                        string.Equals(r.Status, "absent", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.Status, "deleted", StringComparison.OrdinalIgnoreCase));
                    if (problem != null)
                    {
                        log($"Recording {problem.Sid} won't be downloadable (status: {problem.Status}).");
                        return null;
                    }

                    await Task.Delay(2000);
                }

                if (sids.Count == 0)
                {
                    log("The recording is still processing after waiting - it may just need more time for a " +
                        "longer call. Try again in a bit, or reach out if this keeps happening.");
                    return null;
                }

                string? lastSavedPath = null;
                foreach (var sid in sids)
                {
                    TwilioDownloadResult download;
                    int downloadAttempt = 0;
                    do
                    {
                        download = await _twilioClient.DownloadRecordingAsync(sid);
                        if (download.Success) break;
                        downloadAttempt++;
                        if (downloadAttempt < 3) await Task.Delay(2000);
                    } while (downloadAttempt < 3);

                    if (!download.Success || download.Data == null)
                    {
                        log($"Could not download recording {sid}: {download.ErrorMessage}");
                        continue;
                    }

                    var fileName = $"{SanitizeFileName(fileLabel)}_{DateTime.Now:yyyyMMdd_HHmmss}.mp3";
                    var filePath = Path.Combine(recordingsFolder, fileName);
                    try
                    {
                        await File.WriteAllBytesAsync(filePath, download.Data);
                    }
                    catch (Exception ex)
                    {
                        // Couldn't write locally (e.g. network folder unreachable, disk full).
                        // Leave the Twilio copy in place - it's the only copy right now.
                        log($"WARNING: Could not save recording {sid} to {filePath} ({ex.Message}). " +
                            "It was NOT deleted from Twilio, so it isn't lost - fix the recording location and contact the maintainer to retrieve it.");
                        continue;
                    }

                    if (!IsLocalCopyVerified(filePath, download.Data.LongLength))
                    {
                        log($"WARNING: Recording {sid} was written to {filePath} but the saved file doesn't match what was downloaded. " +
                            "It was NOT deleted from Twilio, so it isn't lost.");
                        continue;
                    }

                    log($"Recording saved: {filePath}");
                    lastSavedPath = filePath;
                    SaveStepsSidecar(filePath, scriptSteps);

                    var deleteResult = await _twilioClient.DeleteRecordingAsync(sid);
                    if (deleteResult.Success)
                    {
                        log($"Removed recording {sid} from Twilio (local copy verified).");
                    }
                    else
                    {
                        log($"WARNING: Recording saved locally, but it could NOT be removed from Twilio ({deleteResult.ErrorMessage}). " +
                            "It will stay in Twilio (and keep incurring storage charges) until deleted. Most likely the API key in " +
                            "config.json is missing Delete permission on Voice > Recordings - add it in the Twilio Console.");
                    }

                    if (_transcriptionSettings.Enabled)
                    {
                        await TranscribeRecordingToLogAsync(filePath, log, scriptSteps);
                    }
                }

                return lastSavedPath;
            }
            catch (Exception ex)
            {
                log($"Error fetching recording: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts a saved recording to text and writes it, line by line, through the same
        /// log callback as everything else (so it lands in the status box and the log file),
        /// plus a .txt beside the .mp3. Never throws - a transcription problem must not turn
        /// a successful test call into a failed one.
        /// </summary>
        private async Task TranscribeRecordingToLogAsync(string mp3Path, Action<string> log, IReadOnlyList<Step>? scriptSteps)
        {
            try
            {
                if (!await RecordingTranscriber.EnsureModelAsync(_transcriptionSettings.ModelName, log))
                    return;

                log($"Transcribing {Path.GetFileName(mp3Path)}...");
                var sw = Stopwatch.StartNew();
                var lines = await RecordingTranscriber.TranscribeAsync(mp3Path, _transcriptionSettings, scriptSteps);

                log($"----- Transcript: {Path.GetFileName(mp3Path)} ({sw.Elapsed.TotalSeconds:0}s to transcribe) -----");
                if (lines.Count == 0)
                {
                    log("  (no speech detected)");
                }
                foreach (var line in lines)
                {
                    log($"  {line}");
                }
                log("----- End of transcript -----");

                if (_transcriptionSettings.SaveTranscriptFile)
                {
                    var txtPath = RecordingTranscriber.SaveTranscriptFile(mp3Path, lines);
                    log($"Transcript saved: {txtPath}");
                }
            }
            catch (Exception ex)
            {
                log($"Recording saved, but it couldn't be transcribed: {ex.Message}");
            }
        }

        private void PlayRecordingCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _playbackSettings.PlayRecordingWhenFinished = PlayRecordingCheckBox.IsChecked == true;
            _playbackSettings.Save();
            AppendStatus(_playbackSettings.PlayRecordingWhenFinished
                ? "Recordings will open for playback when a script finishes."
                : "Recordings won't open automatically - they're still saved to the Recording Location.");
        }

        private void TranscribeRecordingsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _transcriptionSettings.Enabled = TranscribeRecordingsCheckBox.IsChecked == true;
            _transcriptionSettings.Save();
            AppendStatus(_transcriptionSettings.Enabled
                ? "Transcribe Recording is on - each call's recording will be transcribed into the log."
                : "Transcribe Recording is off - use \"Transcribe a Saved Recording...\" to transcribe one later.");
        }

        /// <summary>
        /// On-demand transcription of a recording that's already on disk - for calls made
        /// with Transcribe Recording off. If the call's steps were saved alongside it, the
        /// Numeric / Hang Up steps are merged in exactly as they would have been live.
        /// </summary>
        private async void TranscribeSavedRecording_Click(object sender, RoutedEventArgs e)
        {
            var startFolder = _scriptRecordingsFolder ?? CurrentRecordingsFolder();
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a recording to transcribe",
                Filter = "Recordings (*.mp3)|*.mp3|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(startFolder) ? startFolder : DefaultRecordingsFolder
            };
            if (dialog.ShowDialog(this) != true) return;

            var mp3Path = dialog.FileName;
            var steps = LoadStepsSidecar(mp3Path);
            if (steps == null)
            {
                AppendStatus("No saved steps found for this recording (it may pre-date this feature), so the " +
                             "transcript will show what was heard only - no Tester Numeric / Hang Up lines.");
            }

            TranscribeSavedRecordingButton.IsEnabled = false;
            try
            {
                await TranscribeRecordingToLogAsync(mp3Path, AppendStatus, steps);

                var txtPath = Path.ChangeExtension(mp3Path, ".txt");
                if (_transcriptionSettings.SaveTranscriptFile && File.Exists(txtPath))
                {
                    try { Process.Start(new ProcessStartInfo(txtPath) { UseShellExecute = true }); }
                    catch { /* it's in the log either way */ }
                }
            }
            finally
            {
                TranscribeSavedRecordingButton.IsEnabled = true;
            }
        }

        // The script's steps are saved next to each recording (Call_20260930_101500.steps.json)
        // so a later on-demand transcription can still place the Numeric / Hang Up steps.
        private static string StepsSidecarPath(string mp3Path) => Path.ChangeExtension(mp3Path, ".steps.json");

        private static void SaveStepsSidecar(string mp3Path, IReadOnlyList<Step>? steps)
        {
            if (steps == null || steps.Count == 0) return;
            try
            {
                var data = steps.Select(s => new ScriptStepData
                {
                    Type = s.Type.ToString(),
                    Value = s.Value,
                    PauseSecondsBefore = s.PauseSecondsBefore
                }).ToList();
                File.WriteAllText(StepsSidecarPath(mp3Path),
                    System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Only needed for later on-demand transcription - never worth failing a call over.
            }
        }

        private static List<Step>? LoadStepsSidecar(string mp3Path)
        {
            try
            {
                var path = StepsSidecarPath(mp3Path);
                if (!File.Exists(path)) return null;
                var data = System.Text.Json.JsonSerializer.Deserialize<List<ScriptStepData>>(File.ReadAllText(path));
                return data?.Select(s => new Step
                {
                    Type = Enum.TryParse<StepType>(s.Type, out var t) ? t : StepType.Numeric,
                    Value = s.Value,
                    PauseSecondsBefore = s.PauseSecondsBefore > 0 ? s.PauseSecondsBefore : 3
                }).ToList();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Re-checks the file on disk (not our in-memory copy) before anything is
        /// deleted from Twilio - catches silent failures on network drives and the like.</summary>
        private static bool IsLocalCopyVerified(string filePath, long expectedBytes)
        {
            try
            {
                var info = new FileInfo(filePath);
                return info.Exists && expectedBytes > 0 && info.Length == expectedBytes;
            }
            catch
            {
                return false;
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // ---------- Batch: Create ----------

        private void CreateBatch_Click(object sender, RoutedEventArgs e)
        {
            bool opening = CreateBatchPanel.Visibility != Visibility.Visible;
            CreateBatchPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            if (!opening) return;

            BatchScriptFilterBox.Text = string.Empty;
            RefreshBatchChecklist();
        }

        /// <summary>
        /// Abandons the batch being built: closes the panel AND discards whatever was
        /// checked, so reopening starts fresh. (Just collapsing the panel wouldn't be enough -
        /// RefreshBatchChecklist deliberately preserves checked items across refreshes, so
        /// the old selections would silently come back next time.)
        /// </summary>
        private void CancelCreateBatch_Click(object sender, RoutedEventArgs e)
        {
            _batchChecklistView = null; // first, so clearing the filter text below is a harmless no-op
            _batchChecklistSource = new List<CheckableScriptEntry>();
            BatchScriptChecklist.ItemsSource = null;
            BatchScriptFilterBox.Text = string.Empty;
            SaveBatchErrorText.Visibility = Visibility.Collapsed;
            CreateBatchPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Rebuilds the Create Batch checklist from the registry, preserving which
        /// scripts were already checked. Called when the panel first opens, and again
        /// whenever a script is saved or deleted elsewhere while the panel is open, so
        /// newly-saved scripts show up without having to close and reopen the panel.
        /// </summary>
        private void RefreshBatchChecklist()
        {
            var previouslyChecked = new HashSet<string>(
                _batchChecklistSource.Where(s => s.IsChecked).Select(s => s.Name),
                StringComparer.OrdinalIgnoreCase);

            var registry = AppRegistry.Load();
            _batchChecklistSource = registry.Scripts
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new CheckableScriptEntry
                {
                    Name = s.Name,
                    FilePath = s.FilePath,
                    IsChecked = previouslyChecked.Contains(s.Name)
                })
                .ToList();

            _batchChecklistView = CollectionViewSource.GetDefaultView(_batchChecklistSource);
            BatchScriptChecklist.ItemsSource = _batchChecklistView;

            var filter = BatchScriptFilterBox.Text.Trim();
            _batchChecklistView.Filter = filter.Length == 0
                ? null
                : (obj => obj is CheckableScriptEntry entry && entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

            if (_batchChecklistSource.Count == 0)
                AppendBatchStatus("No saved scripts found yet. Use \"Save Script\" on the left to create some first.");
        }

        /// <summary>Refreshes the checklist only if the Create Batch panel is currently open.</summary>
        private void RefreshBatchChecklistIfOpen()
        {
            if (CreateBatchPanel.Visibility == Visibility.Visible)
                RefreshBatchChecklist();
        }

        private void BatchScriptFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_batchChecklistView == null) return;
            var q = BatchScriptFilterBox.Text.Trim();
            _batchChecklistView.Filter = q.Length == 0
                ? null
                : (obj => obj is CheckableScriptEntry entry && entry.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            _batchChecklistView.Refresh();
        }

        // ---------- Batch: Save ----------

        private void SaveBatch_Click(object sender, RoutedEventArgs e)
        {
            SaveBatchErrorText.Visibility = Visibility.Collapsed;

            var chosen = _batchChecklistSource.Where(s => s.IsChecked).ToList();
            if (chosen.Count == 0)
            {
                ShowSaveBatchError("Select at least one script before saving a batch.");
                return;
            }
            if (chosen.Count > UsageTracker.MaxBatchScripts)
            {
                ShowSaveBatchError($"You selected {chosen.Count} scripts. {UsageTracker.BatchSizeLimitMessage}");
                return;
            }

            var dialog = new SaveNameDialog("Save Batch", "Batch name", "Batch-", AppPaths.BatchDir(EnvironmentContext.Current), ".json")
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.ResultPath == null) return;

            var batch = new BatchFile
            {
                Scripts = chosen.Select(c => new BatchScriptRef { Name = c.Name, FilePath = c.FilePath }).ToList()
            };

            try
            {
                batch.Save(dialog.ResultPath);

                var name = Path.GetFileNameWithoutExtension(dialog.ResultPath);
                var registry = AppRegistry.Load();
                registry.AddOrUpdateBatch(name, dialog.ResultPath);
                registry.Save();

                PopulateBatchResults(BatchSearchBox.Text);
                AppendBatchStatus($"Batch saved as \"{name}\" with {chosen.Count} script(s) -> {dialog.ResultPath}");
            }
            catch (Exception ex)
            {
                AppendBatchStatus($"Could not save batch: {ex.Message}");
            }
        }

        // ---------- Batch: Run ----------

        private void BatchSearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => PopulateBatchResults(BatchSearchBox.Text);

        private void PopulateBatchResults(string query)
        {
            var registry = AppRegistry.Load();
            BatchResultsList.ItemsSource = registry.SearchBatches(query);
        }

        private void BatchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BatchResultsList.SelectedItem is not RegistryEntry entry)
            {
                BatchScriptsPreviewList.ItemsSource = null;
                DeleteBatchButton.Visibility = Visibility.Collapsed;
                ChangeBatchLocationButton.IsEnabled = false;
                BatchLocationDisplay.Text = "(no batch selected)";
                return;
            }

            DeleteBatchButton.Visibility = Visibility.Visible;
            ChangeBatchLocationButton.IsEnabled = true;
            // Show the folder the batch lives in (the "location"), not the file name -
            // Change... moves the file between folders, so the folder is what matters here.
            BatchLocationDisplay.Text = Path.GetDirectoryName(entry.FilePath) ?? entry.FilePath;

            try
            {
                var batch = BatchFile.Load(entry.FilePath);
                BatchScriptsPreviewList.ItemsSource = batch.Scripts.Select(s => s.Name).ToList();
            }
            catch (Exception ex)
            {
                BatchScriptsPreviewList.ItemsSource = new List<string> { $"(could not load batch: {ex.Message})" };
            }
        }

        /// <summary>
        /// Moves the selected batch's file to another folder and repoints the registry at it.
        /// Copy first, confirm the copy, then remove the original - so a failure part-way
        /// never leaves the batch without a file. Schedules find batches by name, so they
        /// keep working after a move.
        /// </summary>
        private void ChangeBatchLocation_Click(object sender, RoutedEventArgs e)
        {
            if (BatchResultsList.SelectedItem is not RegistryEntry entry)
            {
                AppendBatchStatus("Select a batch from the list first.");
                return;
            }
            if (!File.Exists(entry.FilePath))
            {
                AppendBatchStatus($"Can't move batch \"{entry.Name}\" - its file wasn't found at {entry.FilePath}.");
                return;
            }

            var currentFolder = Path.GetDirectoryName(entry.FilePath);
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = $"Choose a new folder for batch \"{entry.Name}\" (local or network drive)",
                UseDescriptionForTitle = true,
                SelectedPath = currentFolder != null && Directory.Exists(currentFolder)
                    ? currentFolder
                    : AppPaths.BatchDir(EnvironmentContext.Current)
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var newPath = Path.Combine(Path.GetFullPath(dialog.SelectedPath), Path.GetFileName(entry.FilePath));
            if (string.Equals(Path.GetFullPath(entry.FilePath), newPath, StringComparison.OrdinalIgnoreCase))
            {
                AppendBatchStatus($"Batch \"{entry.Name}\" is already in that folder.");
                return;
            }
            if (File.Exists(newPath))
            {
                var overwrite = System.Windows.MessageBox.Show(this,
                    $"A file named \"{Path.GetFileName(newPath)}\" already exists in that folder:\n{newPath}\n\nReplace it?",
                    "Move Batch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            try
            {
                File.Copy(entry.FilePath, newPath, overwrite: true);
                _ = BatchFile.Load(newPath); // confirm the copy is readable before touching the original
            }
            catch (Exception ex)
            {
                AppendBatchStatus($"Couldn't move batch \"{entry.Name}\" ({ex.Message}). It's unchanged at {entry.FilePath}.");
                return;
            }

            var registry = AppRegistry.Load();
            registry.AddOrUpdateBatch(entry.Name, newPath);
            registry.Save();

            try
            {
                File.Delete(entry.FilePath);
            }
            catch (Exception ex)
            {
                AppendBatchStatus($"Batch now uses the new location, but the old copy couldn't be removed ({ex.Message}): {entry.FilePath}");
            }

            AppendBatchStatus($"Moved batch \"{entry.Name}\" to: {newPath}");

            // Refresh the list and keep the same batch selected.
            PopulateBatchResults(BatchSearchBox.Text);
            BatchResultsList.SelectedItem = (BatchResultsList.ItemsSource as IEnumerable<RegistryEntry>)?
                .FirstOrDefault(b => string.Equals(b.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        }

        private void DeleteBatch_Click(object sender, RoutedEventArgs e)
        {
            if (BatchResultsList.SelectedItem is not RegistryEntry entry)
            {
                AppendBatchStatus("Select a batch from the list first.");
                return;
            }

            var confirm = System.Windows.MessageBox.Show(this,
                $"Delete batch \"{entry.Name}\"? This deletes the file itself:\n{entry.FilePath}\n\n" +
                "(The scripts it references are not deleted, only the batch definition.)",
                "Delete Batch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                if (File.Exists(entry.FilePath))
                    File.Delete(entry.FilePath);
            }
            catch (Exception ex)
            {
                AppendBatchStatus($"Could not delete the file on disk ({ex.Message}), but removing it from the list.");
            }

            var registry = AppRegistry.Load();
            registry.Batches.RemoveAll(b => string.Equals(b.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            registry.Save();

            PopulateBatchResults(BatchSearchBox.Text);
            BatchScriptsPreviewList.ItemsSource = null;
            DeleteBatchButton.Visibility = Visibility.Collapsed;
            ChangeBatchLocationButton.IsEnabled = false;
            AppendBatchStatus($"Deleted batch \"{entry.Name}\".");
        }

        private class BatchRunResult
        {
            public bool Cancelled;
            public bool LimitReached;
            public List<string> Problems = new();
            public bool AnyRecordingSaved;
        }

        /// <summary>
        /// Runs every script in a batch, in order, waiting for each call to finish before
        /// starting the next - the actual dial/poll/record/delete logic, with no UI
        /// dependencies (no button states, no blocking dialogs). Both the interactive
        /// Run Batch button and the scheduler call this same method, so there's exactly
        /// one place that logic lives.
        /// </summary>
        private async Task<BatchRunResult> RunBatchCoreAsync(BatchFile batch, string batchLabel, Action<string> log, Func<bool> isCancelled)
        {
            var result = new BatchRunResult();
            const bool record = true; // always on - no longer optional

            for (int i = 0; i < batch.Scripts.Count; i++)
            {
                if (isCancelled())
                {
                    log($"Batch cancelled - {batch.Scripts.Count - i} script(s) skipped.");
                    result.Cancelled = true;
                    break;
                }

                var scriptRef = batch.Scripts[i];
                string tag = $"[{i + 1}/{batch.Scripts.Count}] {scriptRef.Name}";
                log($"{tag}: loading script...");

                ScriptFile script;
                try
                {
                    script = ScriptFile.Load(scriptRef.FilePath);
                }
                catch (Exception ex)
                {
                    log($"{tag}: could not load ({ex.Message}). Skipping.");
                    result.Problems.Add($"{scriptRef.Name}: could not load the script file.");
                    continue;
                }

                if (!Regex.IsMatch(script.PhoneNumber, "^[0-9]{10}$"))
                {
                    log($"{tag}: invalid or missing phone number in script. Skipping.");
                    result.Problems.Add($"{scriptRef.Name}: invalid or missing phone number.");
                    continue;
                }

                var steps = script.Steps.Select(s => new Step
                {
                    Type = Enum.TryParse<StepType>(s.Type, out var t) ? t : StepType.Numeric,
                    Value = s.Value,
                    PauseSecondsBefore = s.PauseSecondsBefore > 0 ? s.PauseSecondsBefore : 3
                }).ToList();

                if (_remainingCredits is int creditsLeft && creditsLeft <= 0)
                {
                    log($"{tag}: {UsageTracker.LimitMessage} Stopping batch here.");
                    result.LimitReached = true;
                    break;
                }

                string toE164 = "+1" + script.PhoneNumber;
                log($"{tag}: dialing {toE164}...");

                var callResult = await _twilioClient.PlaceCallAsync(toE164, steps, record);

                if (!callResult.Success || callResult.CallSid == null)
                {
                    log($"{tag}: call failed - {callResult.ErrorMessage}");
                    if (callResult.LimitReached)
                    {
                        _remainingCredits = 0;
                        UpdateCreditsDisplay();
                        result.LimitReached = true;
                        break;
                    }
                    if (callResult.InvalidKey)
                    {
                        DemoConfig.ClearKey();
                        _remainingCredits = null;
                        UpdateCreditsDisplay();
                        result.Problems.Add("The demo key isn't valid. Re-run the batch to enter a new one.");
                        break;
                    }
                    result.Problems.Add($"{scriptRef.Name}: call failed ({callResult.ErrorMessage}).");
                    continue;
                }

                if (callResult.Remaining is int remainingAfter)
                {
                    _remainingCredits = remainingAfter;
                    UpdateCreditsDisplay();
                    log($"{tag}: {remainingAfter} test(s) remaining.");
                }

                log($"{tag}: call placed (SID {callResult.CallSid}), waiting for it to finish...");

                // Poll until the call reaches a terminal state so batch calls run one at a
                // time rather than overlapping. Capped so a stuck call can't hang the whole batch.
                var timeout = TimeSpan.FromMinutes(5);
                var start = DateTime.UtcNow;
                string? lastStatus = null;
                while (DateTime.UtcNow - start < timeout)
                {
                    await Task.Delay(3000);
                    var statusResult = await _twilioClient.GetCallStatusAsync(callResult.CallSid);
                    if (statusResult.Success)
                    {
                        lastStatus = statusResult.Status;
                        if (TwilioClient.IsTerminalStatus(statusResult.Status))
                            break;
                    }
                }
                log($"{tag}: finished (status: {lastStatus ?? "unknown/timed out"}).");

                var savedPath = await FetchAndSaveRecordingAsync(
                    callResult.CallSid, scriptRef.Name, log,
                    scriptSteps: steps);
                if (savedPath != null) result.AnyRecordingSaved = true;
            }

            if (result.Cancelled)
            {
                log($"Batch \"{batchLabel}\" stopped early by user.");
            }
            else
            {
                log($"Batch \"{batchLabel}\" complete.");
            }

            return result;
        }

        private async void RunBatch_Click(object sender, RoutedEventArgs e)
        {
            RunBatchErrorText.Visibility = Visibility.Collapsed;

            if (BatchResultsList.SelectedItem is not RegistryEntry entry)
            {
                AppendBatchStatus("Select a batch from the list first.");
                return;
            }

            if (!EnsureDemoKey())
            {
                AppendBatchStatus("A demo key is needed to run a batch.");
                return;
            }

            BatchFile batch;
            try
            {
                batch = BatchFile.Load(entry.FilePath);
            }
            catch (Exception ex)
            {
                AppendBatchStatus($"Could not load batch \"{entry.Name}\": {ex.Message}");
                return;
            }

            if (batch.Scripts.Count == 0)
            {
                AppendBatchStatus($"Batch \"{entry.Name}\" has no scripts in it.");
                return;
            }
            if (batch.Scripts.Count > UsageTracker.MaxBatchScripts)
            {
                AppendBatchStatus($"Batch \"{entry.Name}\" has {batch.Scripts.Count} scripts. {UsageTracker.BatchSizeLimitMessage}");
                return;
            }
            if (_remainingCredits is int batchCreditsLeft && batchCreditsLeft <= 0)
            {
                System.Windows.MessageBox.Show(this, UsageTracker.LimitMessage, "Testing Limit Reached",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                AppendBatchStatus(UsageTracker.LimitMessage);
                return;
            }

            RunBatchButton.IsEnabled = false;
            DeleteBatchButton.Visibility = Visibility.Collapsed;
            ChangeBatchLocationButton.IsEnabled = false;
            CancelBatchButton.Visibility = Visibility.Visible;
            CancelBatchButton.IsEnabled = true;
            _batchCancelRequested = false;
            AppendBatchStatus($"Starting batch \"{entry.Name}\" ({batch.Scripts.Count} script(s))...");

            var runResult = await RunBatchCoreAsync(batch, entry.Name, AppendBatchStatus, () => _batchCancelRequested);

            if (runResult.LimitReached)
            {
                System.Windows.MessageBox.Show(this, UsageTracker.LimitMessage, "Testing Limit Reached",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            if (runResult.Problems.Count > 0)
            {
                RunBatchErrorText.Text = $"{runResult.Problems.Count} script(s) had problems:\n" + string.Join("\n", runResult.Problems);
                RunBatchErrorText.Visibility = Visibility.Visible;
            }

            if (runResult.AnyRecordingSaved)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(RecordingsFolder) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppendBatchStatus($"Recordings saved, but couldn't open the folder automatically ({ex.Message}). Find them at: {RecordingsFolder}");
                }
            }

            RunBatchButton.IsEnabled = true;
            CancelBatchButton.Visibility = Visibility.Collapsed;
            DeleteBatchButton.Visibility = BatchResultsList.SelectedItem is RegistryEntry ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CancelBatch_Click(object sender, RoutedEventArgs e)
        {
            _batchCancelRequested = true;
            CancelBatchButton.IsEnabled = false;
            AppendBatchStatus("Cancelling... will stop once the current script's call finishes (can't interrupt a call already in progress).");
        }

        // ---------- Log Location ----------

        private void LoadLogSettings()
        {
            _logFilePath = LogSettings.Load().EffectiveLogFilePath;
            UpdateLogLocationUi();
            UpdateScriptLogRecordingUi(); // the per-script row shows this value when it has no override
        }

        private void UpdateLogLocationUi()
        {
            if (string.IsNullOrEmpty(_logFilePath))
            {
                LogLocationDisplay.Text = "No logging turned on";
                LogLocationDisplay.Foreground = System.Windows.Media.Brushes.Gray;
                ChooseLogButton.Content = "Choose...";
                StopLogButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                LogLocationDisplay.Text = _logFilePath;
                LogLocationDisplay.Foreground = System.Windows.Media.Brushes.Black;
                ChooseLogButton.Content = "Change...";
                StopLogButton.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Refreshes the two per-script Log/Recording Location displays to reflect
        /// _scriptLogFilePath/_scriptRecordingsFolder - call after either changes.</summary>
        private void UpdateScriptLogRecordingUi()
        {
            // When there's no per-script override, show the actual default/batch value that
            // will really be used - not a vague "Using default" placeholder.
            // An empty override ("") means Stop was clicked for this script specifically.
            var effectiveScriptLog = _scriptLogFilePath ?? _logFilePath;
            bool scriptLoggingOff = string.IsNullOrEmpty(effectiveScriptLog);
            ScriptLogLocationDisplay.Text = scriptLoggingOff
                ? (_scriptLogFilePath == "" ? "No logging for this script" : "No logging turned on")
                : effectiveScriptLog!;
            ChooseScriptLogButton.Content = scriptLoggingOff ? "Choose..." : "Change...";
            StopScriptLogButton.Visibility = scriptLoggingOff ? Visibility.Collapsed : Visibility.Visible;
            ResetScriptLogButton.Visibility = _scriptLogFilePath != null ? Visibility.Visible : Visibility.Collapsed;

            ScriptRecordingLocationDisplay.Text = _scriptRecordingsFolder ?? CurrentRecordingsFolder();
            ResetScriptRecordingButton.Visibility = _scriptRecordingsFolder != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChooseScriptLogLocation_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Choose a log file just for this script",
                FileName = string.IsNullOrEmpty(_scriptLogFilePath) ? "DialScript-log.txt" : Path.GetFileName(_scriptLogFilePath),
                InitialDirectory = string.IsNullOrEmpty(_scriptLogFilePath) ? AppPaths.LogsDir(EnvironmentContext.Current) : Path.GetDirectoryName(_scriptLogFilePath),
                Filter = "Log/Text files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
                OverwritePrompt = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                _scriptLogFilePath = dialog.FileName;
                UpdateScriptLogRecordingUi();
                AppendStatus($"This script will log to: {_scriptLogFilePath} (takes effect once saved).");
            }
        }

        private void StopScriptLogging_Click(object sender, RoutedEventArgs e)
        {
            // Stored as an empty override, which Dial_Click already treats as "don't log"
            // (WriteToLogFile skips empty paths). Use Default switches it back.
            _scriptLogFilePath = string.Empty;
            UpdateScriptLogRecordingUi();
            AppendStatus("This script won't write to a log file (takes effect once saved). Click Use Default to log again.");
        }

        private void ResetScriptLogLocation_Click(object sender, RoutedEventArgs e)
        {
            _scriptLogFilePath = null;
            UpdateScriptLogRecordingUi();
            AppendStatus("This script will use the default log location again (takes effect once saved).");
        }

        private void ChooseScriptRecordingLocation_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose where this script's recordings are saved",
                UseDescriptionForTitle = true,
                RootFolder = Environment.SpecialFolder.MyComputer,
                SelectedPath = _scriptRecordingsFolder != null && Directory.Exists(_scriptRecordingsFolder)
                    ? _scriptRecordingsFolder
                    : CurrentRecordingsFolder()
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _scriptRecordingsFolder = Path.GetFullPath(dialog.SelectedPath);
                UpdateScriptLogRecordingUi();
                AppendStatus($"This script will save recordings to: {_scriptRecordingsFolder} (takes effect once saved).");
            }
        }

        private void ResetScriptRecordingLocation_Click(object sender, RoutedEventArgs e)
        {
            _scriptRecordingsFolder = null;
            UpdateScriptLogRecordingUi();
            AppendStatus("This script will use the default recording location again (takes effect once saved).");
        }

        private void ChooseLogLocation_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Choose log file location",
                FileName = string.IsNullOrEmpty(_logFilePath) ? "DialScript-log.txt" : Path.GetFileName(_logFilePath),
                InitialDirectory = string.IsNullOrEmpty(_logFilePath) ? AppPaths.LogsDir(EnvironmentContext.Current) : Path.GetDirectoryName(_logFilePath),
                Filter = "Log/Text files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
                OverwritePrompt = false // appending to an existing log file is expected, not an overwrite
            };

            if (dialog.ShowDialog(this) == true)
            {
                _logFilePath = dialog.FileName;
                new LogSettings { LogFilePath = _logFilePath }.Save();
                UpdateLogLocationUi();
                UpdateScriptLogRecordingUi();
                AppendStatus($"Logging enabled -> {_logFilePath}");
            }
        }

        private void StopLogging_Click(object sender, RoutedEventArgs e)
        {
            _logFilePath = null;
            new LogSettings { LogFilePath = null, LoggingDisabled = true }.Save();
            UpdateLogLocationUi();
            UpdateScriptLogRecordingUi();
            AppendStatus("Logging turned off.");
        }

        private void WriteToLogFile(string line)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;
            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); // e.g. the default Logs folder on a fresh install
                File.AppendAllText(_logFilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch
            {
                // A log write failure (e.g. an unreachable network path) shouldn't break
                // the app or spam more status messages - just skip this line silently.
            }
        }

        // ---------- Status logs ----------

        private void AppendStatus(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            StatusBox.Text += (StatusBox.Text.Length > 0 ? Environment.NewLine : "") + line;
            StatusBox.ScrollToEnd();
            WriteToLogFile(line);
        }

        private void ShareScripts_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ShareScriptsHelpDialog { Owner = this };
            dialog.ShowDialog();
        }

        private void ClearStatus_Click(object sender, RoutedEventArgs e)
        {
            StatusBox.Text = string.Empty;
        }

        private void ClearBatchStatus_Click(object sender, RoutedEventArgs e)
        {
            BatchStatusBox.Text = string.Empty;
        }

        private void AppendBatchStatus(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            BatchStatusBox.Text += (BatchStatusBox.Text.Length > 0 ? Environment.NewLine : "") + line;
            BatchStatusBox.ScrollToEnd();
            WriteToLogFile($"[Batch] {line}");
        }
    }
}
