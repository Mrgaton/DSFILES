using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSFiles_Shared
{
    public sealed class WebHookHelper
    {
        private HttpClient _client { get; set; }

        private static string WebHookUrl = string.Empty;

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
                if ((int)response.StatusCode != 200) throw new Exception("Could not find webhook");

                ParseJson(response.Content.ReadAsStringAsync().Result);
            }
        }

        public WebHookHelper() { }
        public WebHookHelper(HttpClient client, string url)
        {
            _client = client;

            using (HttpResponseMessage response = MakeRequest(HttpMethod.Get, WebHookUrl = url).Result)
            {
                if ((int)response.StatusCode != 200) throw new Exception("Could not find webhook");

                ParseJson(response.Content.ReadAsStringAsync().Result);
            }
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
                    if (i > splitted.Length - 1) break;

                    line = splitted[i];

                    if (sb.Length + line.Length > 2000) break;

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
            (await MakeRequest(HttpMethod.Post, WebHookUrl, "{\"content\":" + (content) + ",\"username\":\"" + username + "\",\"avatar\":\"" + avatarUrl + "\"}")).StatusCode;

        public async Task<string> GetMessage(ulong id) => await (await MakeRequest(HttpMethod.Get, WebHookUrl + "/messages/" + id)).Content.ReadAsStringAsync();

        public async Task<string> RemoveMessage(ulong id) => await (await MakeRequest(HttpMethod.Delete, WebHookUrl + "/messages/" + id)).Content.ReadAsStringAsync();


        public async Task RemoveMessages(ulong[] ids) => await RemoveMessages(ids, DiscordFilesSpliter.ConsoleProgress);
        public async Task RemoveMessages(ulong[] ids, IProgress<string> p)
        {
            foreach (ulong id in ids)
            {
                if (id <= 0) continue;

                retry:

                p.Report("Removing message id: " + id + '\n');

                var result = await RemoveMessage(id);

                if (result.Length > 0 && result.StartsWith('{') && result.EndsWith('}'))
                {
                    var json = JsonNode.Parse(result);

                    if (json["retry_after"] != null)
                    {
                        Thread.Sleep((int)((double)json["retry_after"] * 1000) + 1);
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

            p.Report("\nDone deleting messages\n");
        }

        public struct FileData
        {
            public string FileName { get; set; }
            public byte[] Data { get; set; }

            public FileData(string fileName, byte[] data)
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
                form.Add(new ByteArrayContent(file.Data, 0, file.Data.Length), index.ToString(), file.FileName);

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
                if (buffer is null) continue;
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

        public async Task<string> PostFileToWebhook(MultipartFormDataContent form)
        {
            using (HttpResponseMessage req = await _client.PostAsync(WebHookUrl, form))
            {
                var response = await req.Content.ReadAsStringAsync();

                return string.IsNullOrEmpty(response) ? req.StatusCode.ToString() : response;
            }
        }

        private async Task<HttpResponseMessage> MakeRequest(HttpMethod method, string url, string content = null, Dictionary<string, string> headers = null)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(method, url))
            {
                if (content != null)
                {
                    req.Content = new StringContent(content);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                if (headers != null) foreach (var header in headers) req.Headers.TryAddWithoutValidation(header.Key, header.Value);

                HttpResponseMessage res = await _client.SendAsync(req);

                if (res.Headers.RetryAfter != null)
                {
                    int time = (int)res.Headers.RetryAfter.Delta.Value.TotalSeconds;

                    Thread.Sleep(time + 1);

                    return await MakeRequest(method, url, content, headers);
                }

                return res;
            }
        }
    }
}