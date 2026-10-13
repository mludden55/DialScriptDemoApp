using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectIVRTester
{
    /// <summary>Remembers the chosen log file path across app restarts. Logging is on by
    /// default (to DefaultLogFilePath) until the user explicitly clicks Stop.</summary>
    public class LogSettings
    {
        public string? LogFilePath { get; set; }

        /// <summary>True only after the user clicks Stop. Kept separate from an empty
        /// LogFilePath so "never chose a location" (use the default) and "turned logging
        /// off" (no logging) aren't confused with each other.</summary>
        public bool LoggingDisabled { get; set; }

        /// <summary>Where logging goes when the user hasn't chosen a location:
        /// %AppData%\DialScript\Production\Logs\DialScript-log.txt.</summary>
        public static string DefaultLogFilePath =>
            Path.Combine(AppPaths.LogsDir("Production"), "DialScript-log.txt");

        /// <summary>The log file to actually use: null when logging was turned off,
        /// otherwise the chosen location or the default.</summary>
        [JsonIgnore]
        public string? EffectiveLogFilePath =>
            LoggingDisabled ? null
            : string.IsNullOrWhiteSpace(LogFilePath) ? DefaultLogFilePath
            : LogFilePath;

        public static string FilePath => Path.Combine(AppPaths.AppDataRoot, "logsettings.json");

        public static LogSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var loaded = JsonSerializer.Deserialize<LogSettings>(json, options);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt file - fall back to the defaults (logging on, default location)
                // rather than crash on startup.
            }
            return new LogSettings();
        }

        public void Save()
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
