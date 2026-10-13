using System.IO;
using System.Text.Json;

namespace ConnectIVRTester
{
    public class EnvironmentSettings
    {
        public List<string> Environments { get; set; } = new() { "Production", "Development", "Integration" };
        public string CurrentEnvironment { get; set; } = "Production";
    }

    /// <summary>
    /// Holds which environment is currently active, ambiently. PhoneBook.FilePath and
    /// AppRegistry.FilePath both read EnvironmentContext.Current to resolve where their
    /// data actually lives - this means every existing Load()/Save() call site throughout
    /// the app keeps working exactly as written, and automatically becomes
    /// environment-aware without needing an explicit parameter threaded through dozens of
    /// call sites. Switching environments is just: set this, then reload.
    /// </summary>
    public static class EnvironmentContext
    {
        public static string Current { get; set; } = "Production";
    }

    /// <summary>Loads/saves the list of environments and which one is currently selected.</summary>
    public static class EnvironmentManager
    {
        private static string FilePath => Path.Combine(AppPaths.AppDataRoot, "environments.json");

        public static EnvironmentSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var loaded = JsonSerializer.Deserialize<EnvironmentSettings>(json, options);
                    if (loaded != null && loaded.Environments.Count > 0) return loaded;
                }
            }
            catch
            {
                // Corrupt file - fall through to a fresh default rather than crash on startup.
            }

            // First run: seed the three default environments and persist immediately so
            // the file exists from here on.
            var fresh = new EnvironmentSettings();
            Save(fresh);
            return fresh;
        }

        public static void Save(EnvironmentSettings settings)
        {
            Directory.CreateDirectory(AppPaths.AppDataRoot);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}
