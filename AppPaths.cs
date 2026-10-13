using System.IO;
using System.Text.RegularExpressions;

namespace ConnectIVRTester
{
    /// <summary>Shared %AppData% path helpers, centralizing the app's folder name (so it
    /// only needs to change in one place) and the per-environment data directory layout.</summary>
    public static class AppPaths
    {
        public const string AppFolderName = "DialScript";

        /// <summary>Folder name used before the app was renamed to DialScript. Kept only so
        /// existing installs can be carried over on first launch - see ResolveAppDataRoot.</summary>
        private const string LegacyAppFolderName = "CallFlowBench";

        /// <summary>Resolved once per process: the first access runs the one-time migration
        /// below, and every later access reuses the result.</summary>
        public static string AppDataRoot { get; } = ResolveAppDataRoot();

        /// <summary>
        /// Returns %AppData%\DialScript, first moving an existing %AppData%\CallFlowBench
        /// folder into place if there is one and the new folder doesn't exist yet. This keeps
        /// everything from the old name - Phone Books, scripts, batches, logs, recordings,
        /// settings, the downloaded speech model and the usage count - instead of starting
        /// the user over with an empty app.
        ///
        /// If the move can't be done (e.g. a file in the old folder is open in another
        /// program), the app keeps using the old folder for this session rather than
        /// silently starting fresh, and tries the move again on the next launch.
        /// </summary>
        private static string ResolveAppDataRoot()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var current = Path.Combine(appData, AppFolderName);
            var legacy = Path.Combine(appData, LegacyAppFolderName);

            if (Directory.Exists(current) || !Directory.Exists(legacy))
                return current;

            try
            {
                Directory.Move(legacy, current);
            }
            catch
            {
                return legacy;
            }

            RewriteSavedPaths(current, legacy, current);
            return current;
        }

        /// <summary>
        /// The Scripts/Batches registries, batch files and scripts store full file paths
        /// (e.g. C:\Users\me\AppData\Roaming\CallFlowBench\Production\Scripts\...). After the
        /// folder is moved those would point at a location that no longer exists, so every
        /// .json file under the new folder has the old folder path swapped for the new one.
        /// Best effort: a file that can't be rewritten is left as it was.
        /// </summary>
        private static void RewriteSavedPaths(string root, string oldPath, string newPath)
        {
            // Paths appear JSON-escaped in these files (each \ written as \\). Only match the
            // old folder when it's followed by a separator or the end of the string, so a
            // longer folder name that merely starts with it is left alone.
            var oldJson = oldPath.Replace("\\", "\\\\");
            var newJson = newPath.Replace("\\", "\\\\");
            var pattern = new Regex(Regex.Escape(oldJson) + @"(?=\\|"")", RegexOptions.IgnoreCase);

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).ToList(); }
            catch { return; }

            foreach (var file in files)
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var updated = pattern.Replace(text, _ => newJson);
                    if (updated != text)
                        File.WriteAllText(file, updated);
                }
                catch
                {
                    // Skip this file; the rest of the migration still stands.
                }
            }
        }

        /// <summary>The folder holding one environment's Phone Book, Scripts/Batches registry,
        /// and seeded sample scripts - e.g. %AppData%\DialScript\Production\.</summary>
        public static string EnvironmentDir(string environment) => Path.Combine(AppDataRoot, environment);

        // The four organized subfolders every environment gets, seeded up front by
        // SampleDataSeeder and used as the default (not mandatory) save location by the
        // relevant dialogs throughout the app. Scripts/batches can still be saved anywhere
        // the user browses to - these are just sensible starting points.
        public static string ScriptsDir(string environment) => Path.Combine(EnvironmentDir(environment), "Scripts");
        public static string BatchDir(string environment) => Path.Combine(EnvironmentDir(environment), "Batch");
        public static string LogsDir(string environment) => Path.Combine(EnvironmentDir(environment), "Logs");
        public static string RecordingsDir(string environment) => Path.Combine(EnvironmentDir(environment), "Recordings");
    }
}
