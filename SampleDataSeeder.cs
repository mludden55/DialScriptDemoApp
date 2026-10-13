using System.IO;

namespace ConnectIVRTester
{
    /// <summary>
    /// Seeds the app with sample scripts/batch/phone-book entry on first run, so a
    /// prospective employer testing this has something to click through immediately
    /// rather than starting from a blank app. Runs at most once per machine (guarded
    /// by a marker file) - deleting the samples afterward is a deliberate user choice
    /// this won't undo. Always seeds into the "Production" environment specifically,
    /// regardless of whichever environment happens to be selected - this only ever runs
    /// on a machine's very first launch, when Production is the environment anyway.
    ///
    /// Sample script files are self-contained (no file-path references inside them),
    /// so they're bundled as-is. The sample batch, however, originally pointed at the
    /// original developer's own absolute file paths - those won't exist on someone
    /// else's machine, so the batch is rebuilt here to point at wherever the scripts
    /// actually get copied to locally, rather than shipping the original batch JSON.
    /// </summary>
    public static class SampleDataSeeder
    {
        private const string Script1Name = "6317918378-TestNumber - Test 1";
        private const string Script2Name = "6317918378-TestNumber - Test 2";
        private const string BatchName = "Batch-Test 1";

        private const string Script1FileName = "TestNumber - Test 1.json";
        private const string Script2FileName = "TestNumber - Test 2.json";

        private const string SeedEnvironment = "Production";

        private static string MarkerPath => Path.Combine(AppPaths.AppDataRoot, "sample_data_seeded.txt");

        public static void SeedIfNeeded()
        {
            if (File.Exists(MarkerPath)) return; // already ran once on this machine

            // Create every configured environment's own (empty) folder up front - derived
            // from the actual environment list rather than hardcoded, so this stays correct
            // even if the default list ever changes. Production gets real data below;
            // Development/Integration (or whatever else is configured) just get an empty
            // folder, so all of them are visible on disk immediately rather than only
            // appearing once someone actually switches to and uses one.
            foreach (var env in EnvironmentManager.Load().Environments)
            {
                Directory.CreateDirectory(AppPaths.EnvironmentDir(env));
                Directory.CreateDirectory(AppPaths.ScriptsDir(env));
                Directory.CreateDirectory(AppPaths.BatchDir(env));
                Directory.CreateDirectory(AppPaths.LogsDir(env));
                Directory.CreateDirectory(AppPaths.RecordingsDir(env));
            }

            // Force Production for the duration of seeding, regardless of whatever
            // EnvironmentContext.Current happens to be, then restore it afterward.
            var previousEnvironment = EnvironmentContext.Current;
            EnvironmentContext.Current = SeedEnvironment;

            try
            {
                var sampleDir = Path.Combine(AppPaths.EnvironmentDir(SeedEnvironment), "SampleScripts");
                Directory.CreateDirectory(sampleDir);

                var bundledDir = Path.Combine(AppContext.BaseDirectory, "SampleData");
                if (!Directory.Exists(bundledDir))
                {
                    File.WriteAllText(MarkerPath, "No bundled SampleData folder found next to the exe - nothing to seed.");
                    return;
                }

                string? script1Path = CopyBundled(bundledDir, sampleDir, Script1FileName);
                string? script2Path = CopyBundled(bundledDir, sampleDir, Script2FileName);

                var registry = AppRegistry.Load();

                if (script1Path != null) registry.AddOrUpdateScript(Script1Name, script1Path);
                if (script2Path != null) registry.AddOrUpdateScript(Script2Name, script2Path);

                if (script1Path != null && script2Path != null)
                {
                    // Rebuilt fresh rather than copying the original batch JSON, since its
                    // FilePath entries pointed at the original developer's machine.
                    var batch = new BatchFile
                    {
                        Scripts = new List<BatchScriptRef>
                        {
                            new() { Name = Script1Name, FilePath = script1Path },
                            new() { Name = Script2Name, FilePath = script2Path }
                        }
                    };
                    var batchPath = Path.Combine(sampleDir, "Batch-Test 1.json");
                    batch.Save(batchPath);
                    registry.AddOrUpdateBatch(BatchName, batchPath);
                }

                registry.Save();

                var phoneBook = PhoneBook.Load();
                if (phoneBook.Entries.Count == 0)
                {
                    phoneBook.AddNumber("6317918378", "Test Number (631)791-8378");
                    phoneBook.Save();
                }

                File.WriteAllText(MarkerPath, $"Seeded sample data at {DateTime.Now}");
            }
            catch (Exception ex)
            {
                // Seeding is a convenience, not a requirement - a failure here should
                // never block the app from starting normally.
                try { File.WriteAllText(MarkerPath, $"Seeding failed: {ex.Message}"); } catch { /* ignore */ }
            }
            finally
            {
                EnvironmentContext.Current = previousEnvironment;
            }
        }

        private static string? CopyBundled(string bundledDir, string destDir, string fileName)
        {
            var src = Path.Combine(bundledDir, fileName);
            if (!File.Exists(src)) return null;
            var dst = Path.Combine(destDir, fileName);
            File.Copy(src, dst, overwrite: true);
            return dst;
        }
    }
}
