using System.IO;
using System.Text.Json;

namespace ConnectIVRTester
{
    /// <summary>Remembers the chosen recordings save folder (if any) across app restarts.
    /// Null/empty means use the default (%AppData%\DialScript\{Environment}\Recordings).
    /// Nested per environment, same as this settings file itself.</summary>
    public class RecordingSettings
    {
        public string? RecordingsFolder { get; set; }

        public static string FilePath =>
            Path.Combine(AppPaths.EnvironmentDir(EnvironmentContext.Current), "recordingsettings.json");

        public static RecordingSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var loaded = JsonSerializer.Deserialize<RecordingSettings>(json, options);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt file - just fall back to the default rather than crash on startup.
            }
            return new RecordingSettings();
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
