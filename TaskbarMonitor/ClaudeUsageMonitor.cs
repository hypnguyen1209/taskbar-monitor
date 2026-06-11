using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace TaskbarMonitor
{
    internal class ClaudeUsageSnapshot
    {
        public float CurrentRatio { get; set; }
        public float WeeklyRatio { get; set; }
        public string CurrentTimeLeft { get; set; } = "--";
        public string WeeklyTimeLeft { get; set; } = "--";
        public bool Ok { get; set; }
        public bool Visible { get; set; }
        public string Status { get; set; } = "unknown";
    }

    internal class ClaudeUsageMonitor
    {
        private static readonly HttpClient Client = CreateClient();
        private readonly object sync = new object();
        private ClaudeUsageSnapshot snapshot = new ClaudeUsageSnapshot();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(30);
            return c;
        }

        public ClaudeUsageSnapshot Snapshot
        {
            get
            {
                lock (sync)
                {
                    return new ClaudeUsageSnapshot
                    {
                        CurrentRatio = snapshot.CurrentRatio,
                        WeeklyRatio = snapshot.WeeklyRatio,
                        CurrentTimeLeft = snapshot.CurrentTimeLeft,
                        WeeklyTimeLeft = snapshot.WeeklyTimeLeft,
                        Ok = snapshot.Ok,
                        Visible = snapshot.Visible,
                        Status = snapshot.Status
                    };
                }
            }
        }

        public async Task RefreshAsync()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string token = ReadAccessToken();
                if (string.IsNullOrWhiteSpace(token))
                {
                    SetSnapshot(new ClaudeUsageSnapshot { Status = "no-token" });
                    return;
                }

                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                    request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.5");
                    request.Content = new StringContent(
                        "{\"model\":\"claude-haiku-4-5-20251001\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                        Encoding.UTF8,
                        "application/json");

                    using (var response = await Client.SendAsync(request).ConfigureAwait(false))
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        float currentRatio = NormalizeRatio(ReadFloatHeader(response, "anthropic-ratelimit-unified-5h-utilization"));
                        float weeklyRatio = NormalizeRatio(ReadFloatHeader(response, "anthropic-ratelimit-unified-7d-utilization"));
                        long currentReset = ReadResetHeader(response, "anthropic-ratelimit-unified-5h-reset");
                        long weeklyReset = ReadResetHeader(response, "anthropic-ratelimit-unified-7d-reset");
                        string status = ReadStringHeader(response, "anthropic-ratelimit-unified-5h-status") ?? response.StatusCode.ToString();

                        SetSnapshot(new ClaudeUsageSnapshot
                        {
                            CurrentRatio = currentRatio,
                            WeeklyRatio = weeklyRatio,
                            CurrentTimeLeft = FormatHoursMinutes(currentReset - now),
                            WeeklyTimeLeft = FormatDaysHours(weeklyReset - now),
                            Ok = response.IsSuccessStatusCode,
                            Visible = response.IsSuccessStatusCode && HasUsageHeaders(response),
                            Status = status
                        });
                    }
                }
            }
            catch
            {
                SetSnapshot(new ClaudeUsageSnapshot { Status = "error" });
            }
        }

        private void SetSnapshot(ClaudeUsageSnapshot value)
        {
            lock (sync)
            {
                snapshot = value;
            }
        }

        private static string ReadAccessToken()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                ".credentials.json");
            if (!File.Exists(path)) return null;

            var json = JObject.Parse(File.ReadAllText(path));
            var token = FindProperty(json, "accessToken");
            return token == null ? null : token.ToString();
        }

        private static JToken FindProperty(JToken token, string name)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                        return property.Value;

                    var nested = FindProperty(property.Value, name);
                    if (nested != null) return nested;
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Children())
                {
                    var nested = FindProperty(item, name);
                    if (nested != null) return nested;
                }
            }

            return null;
        }

        private static string ReadStringHeader(HttpResponseMessage response, string name)
        {
            System.Collections.Generic.IEnumerable<string> values;
            if (response.Headers.TryGetValues(name, out values))
                return values.FirstOrDefault();
            if (response.Content != null && response.Content.Headers.TryGetValues(name, out values))
                return values.FirstOrDefault();
            return null;
        }

        private static float ReadFloatHeader(HttpResponseMessage response, string name)
        {
            string value = ReadStringHeader(response, name);
            float result;
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return result;
            return 0f;
        }

        private static bool HasUsageHeaders(HttpResponseMessage response)
        {
            return ReadStringHeader(response, "anthropic-ratelimit-unified-5h-utilization") != null
                && ReadStringHeader(response, "anthropic-ratelimit-unified-5h-reset") != null
                && ReadStringHeader(response, "anthropic-ratelimit-unified-7d-utilization") != null
                && ReadStringHeader(response, "anthropic-ratelimit-unified-7d-reset") != null;
        }

        private static long ReadResetHeader(HttpResponseMessage response, string name)
        {
            string value = ReadStringHeader(response, name);
            long epoch;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch))
                return epoch;

            DateTimeOffset reset;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out reset))
                return reset.ToUnixTimeSeconds();

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static float NormalizeRatio(float value)
        {
            if (value > 1f && value <= 100f) value = value / 100f;
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        private static string FormatHoursMinutes(long seconds)
        {
            int minutes = Math.Max(0, (int)Math.Round(seconds / 60.0));
            int hours = minutes / 60;
            minutes = minutes % 60;
            return hours > 0 ? "-" + hours + "h" + minutes + "m" : "-" + minutes + "m";
        }

        private static string FormatDaysHours(long seconds)
        {
            int hours = Math.Max(0, (int)Math.Round(seconds / 3600.0));
            int days = hours / 24;
            hours = hours % 24;
            return days > 0 ? "-" + days + "d" + hours + "h" : "-" + hours + "h";
        }
    }
}
