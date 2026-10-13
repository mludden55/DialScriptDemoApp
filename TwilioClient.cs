using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ConnectIVRTester
{
    public class TwilioCallResult
    {
        public bool Success { get; set; }
        public string? CallSid { get; set; }
        public string? ErrorMessage { get; set; }
        /// <summary>True when the server refused the call because this demo key is used up.</summary>
        public bool LimitReached { get; set; }
        /// <summary>True when the server doesn't recognise/accept the demo key at all.</summary>
        public bool InvalidKey { get; set; }
        /// <summary>Runs left on the key after this call, when the server reported it.</summary>
        public int? Remaining { get; set; }
    }

    public class TwilioCallStatusResult
    {
        public bool Success { get; set; }
        public string? Status { get; set; } // queued, ringing, in-progress, completed, busy, failed, no-answer, canceled
        public string? ErrorMessage { get; set; }
    }

    public class TwilioRecordingInfo
    {
        public string Sid { get; set; } = string.Empty;
        public string? Status { get; set; } // processing, completed, absent, deleted, failed
    }

    public class TwilioRecordingsResult
    {
        public bool Success { get; set; }
        public List<TwilioRecordingInfo> Recordings { get; set; } = new();
        public string? ErrorMessage { get; set; }
    }

    public class TwilioDownloadResult
    {
        public bool Success { get; set; }
        public byte[]? Data { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class UsageResult
    {
        public bool Success { get; set; }
        public int Remaining { get; set; }
        public int Limit { get; set; }
        public bool InvalidKey { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Talks to the DialScript demo service (API Gateway + Lambda) instead of the
    /// calling provider directly. The demo build ships with NO provider credentials:
    /// the service holds them, places the calls, and counts runs per demo key.
    /// </summary>
    public class TwilioClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private static readonly HashSet<string> TerminalCallStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "completed", "busy", "failed", "no-answer", "canceled"
        };

        public static bool IsTerminalStatus(string? status) =>
            status != null && TerminalCallStatuses.Contains(status);

        private HttpRequestMessage NewRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, DemoConfig.ApiBaseUrl.TrimEnd('/') + path);
            request.Headers.Add("X-Demo-Key", DemoConfig.DemoKey);
            return request;
        }

        private static string ReadMessage(string body, HttpStatusCode status)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is string s) return s;
            }
            catch (JsonException) { }
            return $"The service returned {(int)status}.";
        }

        private static string ReadCode(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("code", out var c) && c.GetString() is string s) return s;
            }
            catch (JsonException) { }
            return string.Empty;
        }

        public async Task<UsageResult> GetUsageAsync()
        {
            try
            {
                using var request = NewRequest(HttpMethod.Get, "/usage");
                using var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    return new UsageResult
                    {
                        Success = true,
                        Remaining = doc.RootElement.GetProperty("remaining").GetInt32(),
                        Limit = doc.RootElement.GetProperty("limit").GetInt32()
                    };
                }
                return new UsageResult
                {
                    Success = false,
                    InvalidKey = response.StatusCode == HttpStatusCode.Unauthorized,
                    ErrorMessage = ReadMessage(body, response.StatusCode)
                };
            }
            catch (Exception ex)
            {
                return new UsageResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<TwilioCallResult> PlaceCallAsync(string toNumberE164, IEnumerable<Step> steps, bool record = true)
        {
            try
            {
                var payload = new
                {
                    to = toNumberE164,
                    record = record,
                    steps = steps.Select(s => new
                    {
                        type = s.Type.ToString(),
                        value = s.Value ?? string.Empty,
                        pause = s.PauseSecondsBefore
                    }).ToList()
                };

                using var request = NewRequest(HttpMethod.Post, "/calls");
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    int? remaining = doc.RootElement.TryGetProperty("remaining", out var r) && r.TryGetInt32(out var rv) ? rv : null;
                    return new TwilioCallResult
                    {
                        Success = true,
                        CallSid = doc.RootElement.GetProperty("callSid").GetString(),
                        Remaining = remaining
                    };
                }

                var code = ReadCode(body);
                return new TwilioCallResult
                {
                    Success = false,
                    LimitReached = code == "limit_reached",
                    InvalidKey = response.StatusCode == HttpStatusCode.Unauthorized,
                    ErrorMessage = ReadMessage(body, response.StatusCode)
                };
            }
            catch (Exception ex)
            {
                return new TwilioCallResult { Success = false, ErrorMessage = $"Request to the demo service failed: {ex.Message}" };
            }
        }

        public async Task<TwilioCallStatusResult> GetCallStatusAsync(string callSid)
        {
            try
            {
                using var request = NewRequest(HttpMethod.Get, $"/calls/{callSid}");
                using var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    return new TwilioCallStatusResult { Success = false, ErrorMessage = ReadMessage(body, response.StatusCode) };

                using var doc = JsonDocument.Parse(body);
                var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                return new TwilioCallStatusResult { Success = true, Status = status };
            }
            catch (Exception ex)
            {
                return new TwilioCallStatusResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>Lists the recording(s) attached to a call, with each one's status. A
        /// recording's media is only downloadable once its status is "completed".</summary>
        public async Task<TwilioRecordingsResult> GetRecordingSidsAsync(string callSid)
        {
            try
            {
                using var request = NewRequest(HttpMethod.Get, $"/calls/{callSid}/recordings");
                using var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    return new TwilioRecordingsResult { Success = false, ErrorMessage = ReadMessage(body, response.StatusCode) };

                using var doc = JsonDocument.Parse(body);
                var recordings = new List<TwilioRecordingInfo>();
                if (doc.RootElement.TryGetProperty("recordings", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rec in arr.EnumerateArray())
                    {
                        if (rec.TryGetProperty("sid", out var sid) && sid.GetString() is string sidText)
                        {
                            var status = rec.TryGetProperty("status", out var st) ? st.GetString() : null;
                            recordings.Add(new TwilioRecordingInfo { Sid = sidText, Status = status });
                        }
                    }
                }
                return new TwilioRecordingsResult { Success = true, Recordings = recordings };
            }
            catch (Exception ex)
            {
                return new TwilioRecordingsResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>Downloads a recording's audio (MP3) as raw bytes.</summary>
        public async Task<TwilioDownloadResult> DownloadRecordingAsync(string recordingSid)
        {
            try
            {
                using var request = NewRequest(HttpMethod.Get, $"/recordings/{recordingSid}/media");
                using var response = await Http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    return new TwilioDownloadResult { Success = false, ErrorMessage = ReadMessage(body, response.StatusCode) };
                }
                var data = await response.Content.ReadAsByteArrayAsync();
                return new TwilioDownloadResult { Success = true, Data = data };
            }
            catch (Exception ex)
            {
                return new TwilioDownloadResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>Permanently deletes a recording from the calling provider. Only call
        /// this after confirming the local download succeeded.</summary>
        public async Task<TwilioCallResult> DeleteRecordingAsync(string recordingSid)
        {
            try
            {
                using var request = NewRequest(HttpMethod.Delete, $"/recordings/{recordingSid}");
                using var response = await Http.SendAsync(request);
                if (response.IsSuccessStatusCode) return new TwilioCallResult { Success = true };
                var body = await response.Content.ReadAsStringAsync();
                return new TwilioCallResult { Success = false, ErrorMessage = ReadMessage(body, response.StatusCode) };
            }
            catch (Exception ex)
            {
                return new TwilioCallResult { Success = false, ErrorMessage = ex.Message };
            }
        }
    }
}
