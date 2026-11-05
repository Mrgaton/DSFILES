using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace DSFiles_Shared
{
    public sealed class WebHookHelper
    {
        private HttpClient _client { get; set; }

        private string WebHookUrl = string.Empty;

        public ulong applicationId { get; set; }
        public string? avatar { get; set; }
        public string? token { get; set; }
        public ulong channelId { get; set; }
        public ulong guildId { get; set; }
        public ulong id { get; set; }
        public string? name { get; set; }
        public int type { get; set; }

        public WebHookHelper(HttpClient client, ulong webId, string webToken)
        {
            _client = client;

            id = webId;
            token = webToken;

            using (HttpResponseMessage response = MakeRequest(HttpMethod.Get, WebHookUrl = $"https://ptb.discord.com/api/webhooks/{webId}/{webToken}").Result)
            {
                if ((int)response.StatusCode != 200)
                    throw new Exception("Could not find webhook");

                ParseJson(response.Content.ReadAsStringAsync().Result);
            }
        }

        public WebHookHelper()
        {
        }

        public static WebHookHelper FromVerifiedState(HttpClient client, string url, ulong channelId, ulong webhookId, string webhookToken)
        {
            if (!WebhookAddress.IsValid(url))
                throw new ArgumentException("Invalid Discord webhook.", nameof(url));
            var parts = new Uri(url).AbsolutePath.Split('/');
            if (parts[^2] != webhookId.ToString() || parts[^1] != webhookToken || channelId == 0)
                throw new ArgumentException("Webhook metadata does not match its URL.");
            return new WebHookHelper { _client = client, WebHookUrl = url, channelId = channelId, id = webhookId, token = webhookToken };
        }

        public WebHookHelper(HttpClient client, string url)
        {
            if (!WebhookAddress.IsValid(url))
                throw new ArgumentException("A valid HTTPS Discord webhook is required.", nameof(url));
            _client = client;

            using (HttpResponseMessage response = MakeRequest(HttpMethod.Get, WebHookUrl = url).Result)
            {
                if ((int)response.StatusCode != 200)
                    throw new Exception("Could not find webhook");

                ParseJson(response.Content.ReadAsStringAsync().Result);
            }
        }

        public static async Task<WebHookHelper> CreateAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            if (!WebhookAddress.IsValid(url))
                throw new ArgumentException("Invalid Discord webhook.", nameof(url));
            var hook = new WebHookHelper { _client = client, WebHookUrl = url };
            using var response = await hook.MakeRequest(HttpMethod.Get, url, cancellationToken: cancellationToken);
            response.EnsureSuccessStatusCode();
            hook.ParseJson(await response.Content.ReadAsStringAsync(cancellationToken));
            return hook;
        }

        private void ParseJson(string webHookData)
        {
            JsonNode json = JsonNode.Parse(webHookData);

            applicationId = ulong.Parse((json["application_id"] ?? 0).ToString());
            avatar = (json["avatar"] ?? "").ToString();
            channelId = ulong.Parse(json["channel_id"].ToString());
            guildId = ulong.Parse(json["guild_id"].ToString());
            id = ulong.Parse(json["id"].ToString());
            name = json["name"].ToString();
            type = int.Parse(json["type"].ToString());

            token = (string)json["token"];
        }

        public async Task SendMessageInChunks(string content)
        {
            string[] splitted = content.Split('\n').SelectMany(line => line.SplitInParts(2000)).ToArray();

            for (int i = 0; i < splitted.Length; i++)
            {
                StringBuilder sb = new StringBuilder();

                string line = "";

                while (sb.Length + line.Length < 2000)
                {
                    if (i > splitted.Length - 1)
                        break;

                    line = splitted[i];

                    if (sb.Length + line.Length > 2000)
                        break;

                    sb.AppendLine(line);
                    i++;
                }

                i--;

                await SendMessage(sb.ToString().Trim().Replace("\r", ""));
            }
        }

        public async Task<HttpStatusCode> SendMessage(string content) => await SendMessage(content, "");

        public async Task<HttpStatusCode> SendMessage(string content, string username) => await SendMessage(content, username, "");

        public async Task<HttpStatusCode> SendMessage(string content, string username, string avatarUrl) =>
            (await MakeRequest(HttpMethod.Post, WebHookUrl, "{\"content\":" + HttpUtility.JavaScriptStringEncode(content) + ",\"username\":\"" + username + "\",\"avatar\":\"" + avatarUrl + "\"}")).StatusCode;

        public async Task<string> GetMessage(ulong id) => await (await MakeRequest(HttpMethod.Get, WebHookUrl + "/messages/" + id)).Content.ReadAsStringAsync();

        public async Task<string> RemoveMessage(ulong id) => await (await MakeRequest(HttpMethod.Delete, WebHookUrl + "/messages/" + id)).Content.ReadAsStringAsync();

        public async Task RemoveMessages(ulong[] ids) => await RemoveMessages(ids, DiscordFilesSpliter.ConsoleProgress);

        public async Task RemoveMessages(ulong[] ids, IProgress<string> p)
        {
            foreach (ulong id in ids)
            {
                if (id <= 0)
                    continue;

            retry:

                p.Report("Removing message id: " + id);

                var result = await RemoveMessage(id);

                if (result.Length > 0 && result.StartsWith('{') && result.EndsWith('}'))
                {
                    var json = JsonNode.Parse(result);

                    if (json["retry_after"] != null)
                    {
                        await Task.Delay((int)((double)json["retry_after"] * 1000) + 1);
                        goto retry;
                    }
                    else if (!string.IsNullOrEmpty(json.ToString()))
                    {
                        p.Report(json.ToString());
                    }
                }
                else if (!string.IsNullOrEmpty(result))
                {
                    p.Report(result);
                }
            }

            p.Report("\nDone deleting messages");
        }

        public struct FileData
        {
            public string FileName { get; set; }
            public HttpContent Data { get; set; }

            public FileData(string fileName, HttpContent data)
            {
                FileName = fileName;
                Data = data;
            }
        }

        public async Task<string> PostFileToWebhook(FileData[] files)
        {
            MultipartFormDataContent form = new MultipartFormDataContent();

            int index = 0;

            foreach (var file in files)
            {
                form.Add(file.Data, index.ToString(), file.FileName);

                index++;
            }

            return await PostFileToWebhook(form);
        }

        public async Task<string> PostFileToWebhook(string[] fileNames, byte[][] filesData)
        {
            MultipartFormDataContent form = new MultipartFormDataContent();

            for (int i = 0; i < fileNames.Length; i++)
            {
                var buffer = filesData[i];
                if (buffer is null)
                    continue;
                form.Add(new ByteArrayContent(buffer, 0, buffer.Length), i.ToString(), fileNames[i]);
            }

            return await PostFileToWebhook(form);
        }

        public async Task<string> PostFileToWebhook(string fileName, byte[] buffer) => await PostFileToWebhook(fileName, buffer, 0, buffer.Length);

        public async Task<string> PostFileToWebhook(string fileName, byte[] buffer, int offset, int count)
        {
            return await PostFileToWebhook(new MultipartFormDataContent() {
                { new ByteArrayContent(buffer, offset, count), 0.ToString(), fileName}
            });
        }

        public async Task<string> PostFileToWebhook(MultipartFormDataContent form, CancellationToken cancellationToken = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                using HttpResponseMessage req = await _client.PostAsync(WebHookUrl, form, cancellationToken);
                var response = await req.Content.ReadAsStringAsync(cancellationToken);
                if (req.StatusCode != HttpStatusCode.TooManyRequests)
                    return string.IsNullOrEmpty(response) ? req.StatusCode.ToString() : response;
                if (attempt >= 5)
                    req.EnsureSuccessStatusCode();

                TimeSpan delay = req.Headers.RetryAfter?.Delta
                    ?? (req.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromSeconds(JsonNode.Parse(response)?["retry_after"]?.GetValue<double>() ?? 1);
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        private async Task<HttpResponseMessage> MakeRequest(HttpMethod method, string url, string content = null, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(method, url))
            {
                if (content != null)
                {
                    req.Content = new StringContent(content);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                if (headers != null)
                    foreach (var header in headers)
                        req.Headers.TryAddWithoutValidation(header.Key, header.Value);

                HttpResponseMessage res = await _client.SendAsync(req, cancellationToken);

                if (res.Headers.RetryAfter != null)
                {
                    TimeSpan delay = res.Headers.RetryAfter.Delta
                        ?? (res.Headers.RetryAfter.Date!.Value - DateTimeOffset.UtcNow);
                    res.Dispose();
                    await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), cancellationToken);

                    return await MakeRequest(method, url, content, headers, cancellationToken);
                }

                return res;
            }
        }
    }
}
