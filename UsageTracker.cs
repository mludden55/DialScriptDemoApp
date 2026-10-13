namespace ConnectIVRTester
{
    /// <summary>
    /// Demo limits. The run count itself is enforced and stored by the demo service
    /// (per demo key), so nothing here can be reset by deleting local files.
    /// </summary>
    public static class UsageTracker
    {
        public const int MaxBatchScripts = 3;
        public const string ContactEmail = "michaeldludden@gmail.com";

        public static string LimitMessage =>
            "Sorry, you have exceeded your maximum tests for this application. To purchase " +
            $"the application, or request additional testing, send an email to {ContactEmail}.";

        public static string BatchSizeLimitMessage =>
            $"Batches are currently limited to at most {MaxBatchScripts} scripts.";
    }
}
