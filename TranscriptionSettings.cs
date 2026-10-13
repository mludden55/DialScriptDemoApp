using System.IO;
using System.Text.Json;

namespace ConnectIVRTester
{
    /// <summary>
    /// Remembers whether downloaded recordings get transcribed into the log, plus a few
    /// tuning knobs. Stored once for the whole app (not per environment), next to
    /// logsettings.json in %AppData%\DialScript\.
    /// </summary>
    public class TranscriptionSettings
    {
        /// <summary>Transcribe each downloaded recording and write the text into the log.</summary>
        public bool Enabled { get; set; } = true; // on by default; untick "Transcribe Recording" to skip the extra time per call

        /// <summary>Whisper model: "tiny.en" (fastest, ~75 MB), "base.en" (default, ~142 MB),
        /// or "small.en" (most accurate, ~466 MB, noticeably slower on CPU).</summary>
        public string ModelName { get; set; } = "base.en";

        /// <summary>Also save the transcript as a .txt file next to the .mp3.</summary>
        public bool SaveTranscriptFile { get; set; } = true;

        public static string FilePath => Path.Combine(AppPaths.AppDataRoot, "transcriptionsettings.json");

        public static TranscriptionSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var loaded = JsonSerializer.Deserialize<TranscriptionSettings>(json, options);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt file - fall back to defaults rather than crash on startup.
            }
            return new TranscriptionSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Not being able to persist a checkbox shouldn't break anything.
            }
        }
    }
}
