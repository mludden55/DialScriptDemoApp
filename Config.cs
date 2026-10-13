using System.IO;

namespace ConnectIVRTester
{
    /// <summary>
    /// Where the demo talks to, and which demo key it uses. The demo build contains no
    /// calling-provider credentials at all - those live only in the demo service.
    /// </summary>
    public static class DemoConfig
    {
        // Set this to the ApiUrl output of the deployed backend before publishing.
        public const string DefaultApiBaseUrl = "https://REPLACE_WITH_API_ID.execute-api.us-east-1.amazonaws.com";

        /// <summary>DIALSCRIPT_API_URL overrides the built-in URL (handy for testing a dev deployment).</summary>
        public static string ApiBaseUrl
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("DIALSCRIPT_API_URL");
                return string.IsNullOrWhiteSpace(env) ? DefaultApiBaseUrl : env.Trim();
            }
        }

        public static bool ApiUrlLooksUnset => ApiBaseUrl.Contains("REPLACE_WITH");

        private static string KeyFilePath => Path.Combine(AppPaths.AppDataRoot, "demo-key.txt");

        private static string _demoKey = LoadKeyFromDisk();

        public static string DemoKey => _demoKey;
        public static bool HasKey => !string.IsNullOrWhiteSpace(_demoKey);

        private static string LoadKeyFromDisk()
        {
            try { return File.Exists(KeyFilePath) ? File.ReadAllText(KeyFilePath).Trim() : string.Empty; }
            catch { return string.Empty; }
        }

        public static void SaveKey(string key)
        {
            _demoKey = key.Trim().ToUpperInvariant();
            Directory.CreateDirectory(AppPaths.AppDataRoot);
            File.WriteAllText(KeyFilePath, _demoKey);
        }

        public static void ClearKey()
        {
            _demoKey = string.Empty;
            try { if (File.Exists(KeyFilePath)) File.Delete(KeyFilePath); } catch { }
        }
    }
}
