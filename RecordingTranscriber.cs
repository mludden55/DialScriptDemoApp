using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace ConnectIVRTester
{
    public class TranscriptLine
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Speaker { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;

        /// <summary>True for lines spoken on the tester's channel (the script's Voice steps).</summary>
        public bool IsTester { get; set; }

        /// <summary>True when the time is worked out from the script's pauses rather than
        /// heard in the audio (Numeric / Hang Up steps). Shown as "[~00:12]".</summary>
        public bool IsEstimated { get; set; }

        public override string ToString() =>
            $"[{(IsEstimated ? "~" : "")}{(int)Start.TotalMinutes:00}:{Start.Seconds:00}] {Speaker}: {Text}";
    }

    /// <summary>
    /// Turns a downloaded call recording (.mp3) into timestamped text, fully offline.
    ///
    /// Uses Whisper (via Whisper.net) running locally on the CPU - the audio never leaves
    /// this machine, and there's no per-minute cost. The model file is downloaded once on
    /// first use into %AppData%\DialScript\Models\ and reused after that.
    ///
    /// Recordings are dual-channel (one track per call leg), so each channel is transcribed
    /// separately and the results are merged in time order. That gives a readable
    /// back-and-forth ("IVR: ... / Tester: ...") instead of two voices mashed together.
    /// </summary>
    public static class RecordingTranscriber
    {
        private const int WhisperSampleRate = 16000;

        // Below this RMS a stretch of audio is effectively silence. Whisper tends to
        // "hallucinate" filler ("Thank you.") over silent audio, so segments quieter than
        // this are dropped, and an entirely quiet channel is skipped outright.
        private const float SilenceRms = 0.004f;

        private static readonly HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly SemaphoreSlim FactoryLock = new(1, 1);
        private static WhisperFactory? _factory;
        private static string? _factoryModelPath;

        public static string ModelsDir => Path.Combine(AppPaths.AppDataRoot, "Models");

        public static string ModelPath(string modelName) =>
            Path.Combine(ModelsDir, $"ggml-{SanitizeModelName(modelName)}.bin");

        private static string SanitizeModelName(string modelName) =>
            Regex.IsMatch(modelName ?? "", @"^[a-z0-9.\-]+$") ? modelName! : "base.en";

        /// <summary>Downloads the Whisper model the first time it's needed. Returns false
        /// (after logging why) if it isn't available and couldn't be fetched.</summary>
        public static async Task<bool> EnsureModelAsync(string modelName, Action<string> log, CancellationToken ct = default)
        {
            var path = ModelPath(modelName);
            if (File.Exists(path)) return true;

            Directory.CreateDirectory(ModelsDir);
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{SanitizeModelName(modelName)}.bin";
            var partPath = path + ".part";
            log($"Downloading speech-to-text model ({SanitizeModelName(modelName)}) - one-time setup, this can take a minute...");

            try
            {
                using var response = await DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    log($"Couldn't download the speech-to-text model (HTTP {(int)response.StatusCode}). " +
                        $"You can download it manually from {url} and save it as {path}.");
                    return false;
                }

                long? total = response.Content.Headers.ContentLength;
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = File.Create(partPath))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int lastReportedPct = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        received += read;
                        if (total is > 0)
                        {
                            int pct = (int)(received * 100 / total.Value);
                            if (pct >= lastReportedPct + 25 && pct < 100)
                            {
                                lastReportedPct = pct - pct % 25;
                                log($"Model download {lastReportedPct}%...");
                            }
                        }
                    }
                }

                File.Move(partPath, path, overwrite: true);
                log($"Speech-to-text model saved to {path}.");
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* best effort */ }
                log($"Couldn't download the speech-to-text model: {ex.Message}. " +
                    $"You can download it manually from {url} and save it as {path}.");
                return false;
            }
        }

        /// <summary>
        /// Transcribes a recording. Heavy work runs on a background thread, so this is safe
        /// to await from the UI. Doesn't log anything itself - the caller decides where
        /// the lines go.
        /// </summary>
        /// <param name="scriptSteps">The script that ran, if known. Its Voice-step text is used to
        /// work out which channel is the tester, and its Numeric / Hang Up steps are added to
        /// the transcript at their (estimated) times, since key presses aren't speech.</param>
        public static async Task<List<TranscriptLine>> TranscribeAsync(
            string mp3Path, TranscriptionSettings settings,
            IReadOnlyList<Step>? scriptSteps = null, CancellationToken ct = default)
        {
            var testerSpokenText = scriptSteps?
                .Where(s => s.Type == StepType.Voice && !string.IsNullOrWhiteSpace(s.Value))
                .Select(s => s.Value).ToList();

            var factory = await GetFactoryAsync(ModelPath(settings.ModelName));

            // Decode + resample on a background thread; this and Whisper are CPU-bound.
            var channels = await Task.Run(() => DecodeToChannels(mp3Path), ct);

            var perChannel = new List<List<TranscriptLine>>();
            for (int c = 0; c < channels.Length; c++)
            {
                var samples = channels[c];
                var lines = new List<TranscriptLine>();
                perChannel.Add(lines);

                if (Rms(samples, 0, samples.Length) < SilenceRms) continue; // nothing said on this leg

                await Task.Run(async () =>
                {
                    using var processor = factory.CreateBuilder()
                        .WithLanguage("en")
                        .Build();

                    using var wav = new MemoryStream(ToWav16kMono(samples));
                    await foreach (var seg in processor.ProcessAsync(wav, ct))
                    {
                        var text = seg.Text?.Trim() ?? string.Empty;
                        if (IsNonSpeech(text)) continue;

                        int from = (int)Math.Clamp(seg.Start.TotalSeconds * WhisperSampleRate, 0, samples.Length);
                        int to = (int)Math.Clamp(seg.End.TotalSeconds * WhisperSampleRate, from, samples.Length);
                        if (to - from > 0 && Rms(samples, from, to) < SilenceRms) continue; // hallucinated over silence

                        // Whisper's segment times are loose - a segment often starts where the
                        // previous one ended, stretching back over silence (and any beep in
                        // it). Pin the line to where these words were actually spoken.
                        var start = seg.Start;
                        var end = seg.End;
                        if (LocateUtterance(samples, from, to, Words(text).Count) is (int uStart, int uEnd))
                        {
                            start = TimeSpan.FromSeconds((double)uStart / WhisperSampleRate);
                            end = TimeSpan.FromSeconds((double)uEnd / WhisperSampleRate);
                        }
                        lines.Add(new TranscriptLine { Start = start, End = end, Text = text });
                    }
                }, ct);
            }

            AssignSpeakers(perChannel, testerSpokenText);

            var merged = perChannel.SelectMany(l => l).OrderBy(l => l.Start).ToList();
            if (scriptSteps is { Count: > 0 })
                merged = AddScriptSteps(merged, scriptSteps);
            return merged;
        }

        /// <summary>Writes the transcript next to the recording (Call_20260930_101500.txt).</summary>
        public static string SaveTranscriptFile(string mp3Path, IReadOnlyList<TranscriptLine> lines)
        {
            var txtPath = Path.ChangeExtension(mp3Path, ".txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Transcript of {Path.GetFileName(mp3Path)}");
            sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} (automatic speech-to-text - may contain errors)");
            sb.AppendLine();
            foreach (var line in lines) sb.AppendLine(line.ToString());
            if (lines.Count == 0) sb.AppendLine("(no speech detected)");
            File.WriteAllText(txtPath, sb.ToString());
            return txtPath;
        }

        // ---------- internals ----------

        private static async Task<WhisperFactory> GetFactoryAsync(string modelPath)
        {
            await FactoryLock.WaitAsync();
            try
            {
                if (_factory != null && _factoryModelPath == modelPath) return _factory;
                _factory?.Dispose();
                _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath));
                _factoryModelPath = modelPath;
                return _factory;
            }
            finally
            {
                FactoryLock.Release();
            }
        }

        /// <summary>Decodes the MP3 to 16 kHz float samples, one array per channel.</summary>
        private static float[][] DecodeToChannels(string mp3Path)
        {
            WaveStream reader;
            try
            {
                reader = new Mp3FileReader(mp3Path);           // Windows' built-in MP3 decoder (ACM)
            }
            catch
            {
                reader = new MediaFoundationReader(mp3Path);   // fallback: Media Foundation
            }

            using (reader)
            {
                int channelCount = reader.WaveFormat.Channels;
                ISampleProvider provider = reader.ToSampleProvider();
                if (provider.WaveFormat.SampleRate != WhisperSampleRate)
                    provider = new WdlResamplingSampleProvider(provider, WhisperSampleRate);

                var perChannel = Enumerable.Range(0, channelCount).Select(_ => new List<float>()).ToArray();
                var buffer = new float[WhisperSampleRate * channelCount];
                int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                        perChannel[i % channelCount].Add(buffer[i]);
                }
                return perChannel.Select(l => l.ToArray()).ToArray();
            }
        }

        /// <summary>Wraps mono float samples in a 16 kHz 16-bit PCM WAV, which is what Whisper expects.</summary>
        private static byte[] ToWav16kMono(float[] samples)
        {
            int dataBytes = samples.Length * 2;
            using var ms = new MemoryStream(44 + dataBytes);
            using var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataBytes);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                        // fmt chunk size
            w.Write((short)1);                  // PCM
            w.Write((short)1);                  // mono
            w.Write(WhisperSampleRate);
            w.Write(WhisperSampleRate * 2);     // byte rate
            w.Write((short)2);                  // block align
            w.Write((short)16);                 // bits per sample
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataBytes);
            foreach (var s in samples)
                w.Write((short)(Math.Clamp(s, -1f, 1f) * short.MaxValue));
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>
        /// Finds where a segment's words were actually spoken, as sample indices - or null
        /// if nothing in [from, to) is clearly above silence (then Whisper's times are kept).
        ///
        /// The audio is split into sound "blocks" separated by 0.7s+ of silence. Whisper's
        /// segment end is the reliable edge, so blocks are taken from the END backwards
        /// until they add up to roughly how long the words take to say. That way stray
        /// sounds earlier in the stretch - an IVR's record beep, a click, line noise - are
        /// left out instead of being mistaken for the start of speech.
        /// </summary>
        private static (int Start, int End)? LocateUtterance(float[] samples, int from, int to, int wordCount)
        {
            const int frame = WhisperSampleRate / 50;           // 20 ms
            const int maxGapFrames = 35;                        // 0.7 s of quiet ends a block
            const int minBlockFrames = 8;                       // < 0.16 s = click/noise, ignore
            const double secondsPerWord = 0.3;                  // brisk IVR / TTS speaking rate

            var blocks = new List<(int Start, int End)>();
            int blockStart = -1, lastVoiced = -1;
            for (int i = from; i + frame <= to; i += frame)
            {
                if (Rms(samples, i, i + frame) < SilenceRms * 3) continue;
                if (blockStart >= 0 && (i - lastVoiced) / frame > maxGapFrames)
                {
                    blocks.Add((blockStart, lastVoiced + frame));
                    blockStart = -1;
                }
                if (blockStart < 0) blockStart = i;
                lastVoiced = i;
            }
            if (blockStart >= 0) blocks.Add((blockStart, lastVoiced + frame));

            blocks.RemoveAll(b => (b.End - b.Start) / frame < minBlockFrames);
            if (blocks.Count == 0) return null;

            double expectedSamples = Math.Max(0.5, wordCount * secondsPerWord) * WhisperSampleRate;
            int first = blocks.Count - 1;
            double covered = blocks[first].End - blocks[first].Start;
            while (first > 0 && covered < expectedSamples * 0.6)
            {
                first--;
                covered += blocks[first].End - blocks[first].Start;
            }
            return (blocks[first].Start, blocks[^1].End);
        }

        /// <summary>
        /// Adds the script's own steps to the transcript. The call follows the script exactly:
        /// for each step Twilio waits the step's Pause Before, then acts - so step times can be
        /// worked out by adding up pauses. Voice steps are also heard in the recording, so each
        /// one found there re-anchors the clock to the real time, keeping later estimates from
        /// drifting (e.g. when the call took a moment to connect or the TTS ran long).
        ///
        /// Voice steps are already in the transcript as heard, so they're relabelled rather
        /// than duplicated. Numeric and Hang Up steps are inserted, marked as estimated.
        /// </summary>
        private static List<TranscriptLine> AddScriptSteps(List<TranscriptLine> lines, IReadOnlyList<Step> steps)
        {
            const double secondsPerDigit = 0.5;   // Twilio's DTMF tone + gap, roughly
            const double spokenWordsPerSecond = 2.5;

            foreach (var l in lines.Where(l => l.IsTester)) l.Speaker = "Tester Voice";

            var testerLines = lines.Where(l => l.IsTester).OrderBy(l => l.Start).ToList();
            int nextTesterLine = 0;
            var added = new List<TranscriptLine>();
            double clock = 0; // seconds since the call was answered (= start of the recording)

            foreach (var step in steps)
            {
                clock += step.PauseSecondsBefore > 0 ? step.PauseSecondsBefore : 3;

                switch (step.Type)
                {
                    case StepType.Numeric:
                        added.Add(new TranscriptLine
                        {
                            Start = TimeSpan.FromSeconds(clock),
                            Speaker = "Tester Numeric",
                            Text = step.Value,
                            IsEstimated = true
                        });
                        clock += step.Value.Sum(ch => ch == 'W' ? 1.0 : secondsPerDigit); // 'w' = 0.5s wait, 'W' = 1s
                        break;

                    case StepType.Voice:
                        var stepWords = Words(step.Value);
                        // Next tester line(s) that say this step's words, not far before where we expect it.
                        int matched = -1;
                        for (int i = nextTesterLine; i < testerLines.Count; i++)
                        {
                            if (testerLines[i].Start.TotalSeconds < clock - 2) continue;
                            if (Words(testerLines[i].Text).Any(stepWords.Contains)) { matched = i; break; }
                        }

                        if (matched >= 0)
                        {
                            // A long Say can be split over several segments - take them all.
                            int last = matched;
                            while (last + 1 < testerLines.Count && Words(testerLines[last + 1].Text).Any(stepWords.Contains))
                                last++;
                            clock = Math.Max(clock, testerLines[last].End.TotalSeconds);
                            nextTesterLine = last + 1;
                        }
                        else
                        {
                            clock += Math.Max(1, stepWords.Count / spokenWordsPerSecond);
                        }
                        break;

                    case StepType.HangUp:
                        added.Add(new TranscriptLine
                        {
                            Start = TimeSpan.FromSeconds(clock),
                            Speaker = "Tester Hang Up",
                            Text = "(script ends the call)",
                            IsEstimated = true
                        });
                        break;
                }
            }

            // Stable sort: at equal times, keep what was heard ahead of what was estimated.
            return lines.Concat(added).OrderBy(l => l.Start).ToList();
        }

        private static float Rms(float[] samples, int from, int to)
        {
            if (to <= from) return 0;
            double sum = 0;
            for (int i = from; i < to; i++) sum += samples[i] * samples[i];
            return (float)Math.Sqrt(sum / (to - from));
        }

        // Whisper marks non-speech as e.g. "[BLANK_AUDIO]", "(music)", "[beep]".
        private static bool IsNonSpeech(string text) =>
            string.IsNullOrWhiteSpace(text) || Regex.IsMatch(text, @"^\s*[\[\(\*].*[\]\)\*]\s*$");

        /// <summary>
        /// Works out which channel is the IVR and which is the tester. The channel order in
        /// the file isn't something to rely on, so this is decided from the content:
        ///
        ///  1. If only one channel has any speech, it's the IVR - the tester side only
        ///     speaks when the script has Voice steps.
        ///  2. Otherwise, the tester channel is the one made up almost entirely of the
        ///     script's Voice-step words. This compares the SHARE of each channel's words
        ///     that come from the script, not the raw count: an IVR that echoes the
        ///     tester's message back (record/playback tests) contains all of those words
        ///     too, plus its own prompts - so by raw count the IVR could win, but by share
        ///     the tester's channel always does.
        ///  3. If that's a tie (e.g. no Voice steps), the channel with less speech is the tester.
        /// </summary>
        private static void AssignSpeakers(List<List<TranscriptLine>> perChannel, IReadOnlyList<string>? testerSpokenText)
        {
            if (perChannel.Count == 1)
            {
                // Mono recording: both sides are mixed together, so no speaker labels.
                foreach (var l in perChannel[0]) l.Speaker = "Call";
                return;
            }

            var channelWords = perChannel
                .Select(lines => Words(string.Join(" ", lines.Select(l => l.Text))))
                .ToList();

            int testerChannel;
            bool has0 = channelWords[0].Count > 0, has1 = channelWords[1].Count > 0;
            if (has0 != has1)
            {
                testerChannel = has0 ? 1 : 0;                                      // rule 1
            }
            else
            {
                var testerWords = Words(string.Join(" ", testerSpokenText ?? Array.Empty<string>()));
                double share0 = ScriptShare(channelWords[0], testerWords);
                double share1 = ScriptShare(channelWords[1], testerWords);
                if (Math.Abs(share0 - share1) > 0.05)
                    testerChannel = share0 > share1 ? 0 : 1;                        // rule 2
                else
                    testerChannel = channelWords[0].Count <= channelWords[1].Count ? 0 : 1; // rule 3
            }

            for (int c = 0; c < perChannel.Count; c++)
            {
                bool isTester = c == testerChannel;
                string label = c > 1 ? $"Channel {c + 1}" : isTester ? "Tester" : "IVR";
                foreach (var l in perChannel[c])
                {
                    l.Speaker = label;
                    l.IsTester = isTester;
                }
            }
        }

        /// <summary>Fraction of a channel's distinct words that appear in the script's Voice steps.</summary>
        private static double ScriptShare(HashSet<string> channelWords, HashSet<string> testerWords) =>
            channelWords.Count == 0 || testerWords.Count == 0
                ? 0
                : (double)channelWords.Count(testerWords.Contains) / channelWords.Count;

        private static HashSet<string> Words(string text) =>
            Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9']{3,}").Select(m => m.Value).ToHashSet();
    }
}
