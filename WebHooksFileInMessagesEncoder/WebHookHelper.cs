using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebHooksFileInMessagesEncoder
{
    public sealed class WebHookHelper
    {
        private static HttpClient client = new HttpClient();

        private static string WebHookUrl = string.Empty;

        public ulong applicationId { get; set; }
        public string avatar { get; set; }
        public string token { get; set; }
        public ulong channelId { get; set; }
        public ulong guildId { get; set; }
        public ulong id { get; set; }
        public string name { get; set; }
        public int type { get; set; }

        public WebHookHelper(ulong webHookId, string token)
        {
            using (HttpResponseMessage response = MakeRequest(HttpMethod.Get, WebHookUrl = $"https://ptb.discord.com/api/webhooks/{webHookId}/{token}").Result)
            {
                if ((int)response.StatusCode != 200) throw new Exception("Could not find webhook");

                ParseJson(response.Content.ReadAsStringAsync().Result);
            }
        }

        public WebHookHelper(string url)
        {
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

        public async Task<HttpStatusCode> SendMessage(string content) => (await SendMessage(content, Application.ProductName));

        public async Task<HttpStatusCode> SendMessage(string content, string username) => (await SendMessage(content, username, ""));

        public async Task<HttpStatusCode> SendMessage(string content, string username, string avatarUrl) => (await MakeRequest(HttpMethod.Post, WebHookUrl, "{\"content\":" + JsonSerializer.Serialize(content) + ",\"username\":\"" + username + "\",\"avatar\":\"" + avatarUrl + "\"}")).StatusCode;

        public async Task<string> GetMessage(ulong id) => await (await MakeRequest(HttpMethod.Get, WebHookUrl + "/messages/" + id)).Content.ReadAsStringAsync();

        public async Task<string> RemoveMessage(ulong id) => await (await MakeRequest(HttpMethod.Delete, WebHookUrl + "/messages/" + id)).Content.ReadAsStringAsync();

        public async Task RemoveMessages(ulong[] ids)
        {
            foreach (ulong id in ids)
            {
                if (id <= 0) continue;

                retry:
                Console.WriteLine("Removing message id: " + id);

                var result = this.RemoveMessage(id).Result;

                if (result.Length > 0 && result.StartsWith('{') && result.EndsWith('}'))
                {
                    var json = JsonNode.Parse(result);

                    if (json["retry_after"] != null)
                    {
                        Thread.Sleep((int)((double)json["retry_after"] * 1000) + 1);
                        goto retry;
                    }
                }
                else if (!string.IsNullOrEmpty(result))
                {
                    Console.WriteLine(result);
                }
            }
        }

        public async Task<string> PostFileToWebhook(byte[] data, string fileName)
        {
            MultipartFormDataContent form = new MultipartFormDataContent
            {
                { new ByteArrayContent(data, 0, data.Length), "0", fileName }
            };

            using (HttpResponseMessage req = await client.PostAsync(WebHookUrl, form))
            {
                return await req.Content.ReadAsStringAsync();
            }
        }

        private static async Task<HttpResponseMessage> MakeRequest(HttpMethod method, string url, string content = null, Dictionary<string, string> headers = null)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(method, url))
            {
                if (content != null)
                {
                    req.Content = new StringContent(content);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                if (headers != null) foreach (var header in headers) req.Headers.TryAddWithoutValidation(header.Key, header.Value);

                HttpResponseMessage res = await client.SendAsync(req);

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