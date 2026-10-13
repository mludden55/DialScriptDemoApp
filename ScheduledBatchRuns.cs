using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectIVRTester
{
    public enum ScheduleType { Single, Daily, Weekly }

    public class ScheduledBatchRun
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Batch names (as registered in this environment's registry) to run,
        /// in order, when this schedule fires.</summary>
        public List<string> BatchNames { get; set; } = new();

        public ScheduleType Type { get; set; } = ScheduleType.Single;

        /// <summary>Used only when Type == Single - the one specific date+time to run.</summary>
        public DateTime? SingleDateTime { get; set; }

        /// <summary>Minutes since midnight - used for Daily and Weekly (and mirrors
        /// SingleDateTime's time-of-day for Single, so the UI has one consistent field).</summary>
        public int TimeOfDayMinutes { get; set; }

        /// <summary>Used only when Type == Weekly.</summary>
        public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

        public bool Enabled { get; set; } = true;

        /// <summary>Dedicated log file for this schedule's runs - separate from the app's
        /// main Log Location, since these fire unattended and need their own audit trail.</summary>
        public string? LogFilePath { get; set; }

        /// <summary>When this schedule last actually fired, so the engine doesn't fire it
        /// twice for the same occurrence (e.g. re-triggering repeatedly within the same
        /// minute, or re-running a Daily/Weekly slot it already ran today/this week).</summary>
        public DateTime? LastRunAt { get; set; }

        /// <summary>
        /// Daily/Weekly only: the specific date+time this schedule will fire next. Set to
        /// the next FUTURE occurrence whenever the schedule is created, enabled, fires, or
        /// the app starts - so a time that has already passed (earlier today, or while the
        /// app was closed) is never run late; it just waits for the next occurrence.
        /// </summary>
        public DateTime? NextRunAt { get; set; }

        /// <summary>Once only: true if its time passed while the app was closed, so it was
        /// skipped (and disabled) instead of being run late.</summary>
        public bool Missed { get; set; }

        [JsonIgnore]
        public bool IsRecurring => Type is ScheduleType.Daily or ScheduleType.Weekly;

        /// <summary>The first Daily/Weekly occurrence strictly after <paramref name="after"/>.</summary>
        public DateTime NextOccurrenceAfter(DateTime after)
        {
            if (Type == ScheduleType.Weekly)
            {
                int daysAhead = ((int)DayOfWeek - (int)after.DayOfWeek + 7) % 7;
                var candidate = after.Date.AddDays(daysAhead).Add(TimeOfDay);
                return candidate > after ? candidate : candidate.AddDays(7);
            }
            else // Daily
            {
                var candidate = after.Date.Add(TimeOfDay);
                return candidate > after ? candidate : candidate.AddDays(1);
            }
        }

        [JsonIgnore]
        public string NextRunText =>
            Missed ? "Not run - the app wasn't open at the scheduled time"
            : !Enabled ? "Next run: - (disabled)"
            : IsRecurring && NextRunAt.HasValue ? $"Next run: {NextRunAt.Value:ddd MMM d} at {NextRunAt.Value:h:mm tt}"
            : Type == ScheduleType.Single && SingleDateTime.HasValue && LastRunAt == null
                ? $"Next run: {SingleDateTime.Value:ddd MMM d} at {SingleDateTime.Value:h:mm tt}"
            : string.Empty;

        [JsonIgnore]
        public TimeSpan TimeOfDay
        {
            get => TimeSpan.FromMinutes(TimeOfDayMinutes);
            set => TimeOfDayMinutes = (int)value.TotalMinutes;
        }

        [JsonIgnore]
        public string ScheduleDescription => Type switch
        {
            ScheduleType.Single => SingleDateTime.HasValue
                ? $"Once, {SingleDateTime.Value:MMM d, yyyy} at {SingleDateTime.Value:h:mm tt}"
                : "Once (no date set)",
            ScheduleType.Daily => $"Daily at {DateTime.Today.Add(TimeOfDay):h:mm tt}",
            ScheduleType.Weekly => $"Weekly on {DayOfWeek}s at {DateTime.Today.Add(TimeOfDay):h:mm tt}",
            _ => "Unknown"
        };

        [JsonIgnore]
        public string BatchesSummary => BatchNames.Count == 0 ? "(no batches selected)" : string.Join(", ", BatchNames);

        [JsonIgnore]
        public string EnableButtonLabel => Enabled ? "Disable" : "Enable";
    }

    public class ScheduledBatchRunsData
    {
        public List<ScheduledBatchRun> Runs { get; set; } = new();
    }

    /// <summary>Loads/saves this environment's scheduled batch runs.</summary>
    public static class ScheduledBatchRunsManager
    {
        public static string FilePath =>
            Path.Combine(AppPaths.EnvironmentDir(EnvironmentContext.Current), "scheduledruns.json");

        public static ScheduledBatchRunsData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var loaded = JsonSerializer.Deserialize<ScheduledBatchRunsData>(json, options);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt file - start fresh rather than crash on startup.
            }
            return new ScheduledBatchRunsData();
        }

        public static void Save(ScheduledBatchRunsData data)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
