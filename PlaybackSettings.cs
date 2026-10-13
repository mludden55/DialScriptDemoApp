using System.IO;
using System.Text.Json;

namespace ConnectIVRTester
{
    /// <summary>Remembers whether a single script's recording opens for playback when the
    /// call finishes. Applies to every script run from the Dial button (not per script);
    /// stored once for the whole app in %AppData%\DialScript\playbacksettings.json.</summary>
    public class PlaybackSettings
    {
        public bool PlayRecordingWhenFinished { get; set; } = true; // matches the app's original behavior

        public static string FilePath => Path.Combine(AppPaths.AppDataRoot, "playbacksettings.json");

        public static PlaybackSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var loaded = JsonSerializer.Deserialize<PlaybackSettings>(File.ReadAllText(FilePath), options);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt file - fall back to the default rather than crash on startup.
            }
            return new PlaybackSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Not being able to persist a checkbox shouldn't break anything.
            }
        }
    }
}
