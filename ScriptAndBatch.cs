using System.IO;
using System.Text.Json;

namespace ConnectIVRTester
{
    internal static class JsonOptions
    {
        public static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
    }

    public class ScriptStepData
    {
        public string Type { get; set; } = "Numeric"; // "Numeric" or "Voice"
        public string Value { get; set; } = string.Empty;
        public int PauseSecondsBefore { get; set; } = 3;
    }

    /// <summary>
    /// A saved test script: the target phone number, hold time, and ordered steps.
    /// Can be saved to any path the user picks (local drive, network share, etc.) -
    /// the file itself is fully self-contained and portable.
    /// </summary>
    public class ScriptFile
    {
        public string PhoneNumber { get; set; } = string.Empty;

        /// <summary>Null means "use the default/batch setting" - only set when this
        /// script has its own override chosen via the controls above the Dial button.</summary>
        public string? LogFilePath { get; set; }

        /// <summary>Same as LogFilePath but for where this script's recording is saved.</summary>
        public string? RecordingsFolder { get; set; }

        public List<ScriptStepData> Steps { get; set; } = new();
        public DateTime SavedAt { get; set; } = DateTime.Now;

        public static ScriptFile Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ScriptFile>(json, JsonOptions.CaseInsensitive)
                   ?? throw new InvalidDataException("Script file is empty or invalid.");
        }

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    public class BatchScriptRef
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// A saved batch: an ordered list of script references (by name + path). Like
    /// scripts, batch files can be saved anywhere; the registry is what makes them
    /// searchable afterward.
    /// </summary>
    public class BatchFile
    {
        public List<BatchScriptRef> Scripts { get; set; } = new();
        public DateTime SavedAt { get; set; } = DateTime.Now;

        public static BatchFile Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BatchFile>(json, JsonOptions.CaseInsensitive)
                   ?? throw new InvalidDataException("Batch file is empty or invalid.");
        }

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    /// <summary>Wraps a registry entry with a checkbox state for the Create Batch checklist.</summary>
    public class CheckableScriptEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public bool IsChecked { get; set; }
    }

    public class RegistryEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Local index of every script and batch ever saved through this app, keyed by
    /// name with a pointer to its actual file path (which may be local or a network
    /// share). This is what lets the Script Selector, Create Batch list, and Run
    /// Batch search work even though the underlying files can live anywhere the user
    /// chose to save them - the registry is the map from "name you searched for" to
    /// "where that file actually is."
    /// </summary>
    public class AppRegistry
    {
        public List<RegistryEntry> Scripts { get; set; } = new();
        public List<RegistryEntry> Batches { get; set; } = new();

        public static string FilePath =>
            Path.Combine(AppPaths.EnvironmentDir(EnvironmentContext.Current), "registry.json");

        public static AppRegistry Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<AppRegistry>(json, JsonOptions.CaseInsensitive);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Corrupt file - start fresh rather than crash on startup.
            }
            return new AppRegistry();
        }

        public void Save()
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }

        public void AddOrUpdateScript(string name, string filePath)
        {
            Scripts.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            Scripts.Add(new RegistryEntry { Name = name, FilePath = filePath, SavedAt = DateTime.Now });
        }

        public void AddOrUpdateBatch(string name, string filePath)
        {
            Batches.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            Batches.Add(new RegistryEntry { Name = name, FilePath = filePath, SavedAt = DateTime.Now });
        }

        public List<RegistryEntry> SearchScripts(string query) => Search(Scripts, query);
        public List<RegistryEntry> SearchBatches(string query) => Search(Batches, query);

        private static List<RegistryEntry> Search(List<RegistryEntry> entries, string query)
        {
            var filtered = string.IsNullOrWhiteSpace(query)
                ? entries
                : entries.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            return filtered.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
