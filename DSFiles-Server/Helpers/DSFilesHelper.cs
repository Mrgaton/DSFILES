using DSFiles_Shared;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSFiles_Server.Helpers
{
    internal static class DSFilesHelper
    {
        private static readonly Dictionary<string, (string refreshedUrl, DateTime time)> cache = new();

        private static readonly int maxCacheSize = 12 * 1000;

        public static async Task<string[]> RefreshUrls(string[] urls)
        {
            if (urls.Length > 50) throw new ArgumentOutOfRangeException(nameof(urls), "Urls length cant be bigger than 50");

            List<string> refreshedUrls = [];
            List<string> urlsToRefresh = [];

            foreach (var url in urls)
            {
                if (cache.ContainsKey(url))
                {
                    var info = cache[url];

                    if ((DateTime.Now - info.time).TotalMinutes <= (23 * 60) + 50)
                    {
                        refreshedUrls.Add(url + info.refreshedUrl);

                        cache.Remove(url);
                    }
                }
                else
                {
                    urlsToRefresh.Add(url);
                }
            }

            if (urlsToRefresh.Count > 0)
            {
                string[] refreshedUrlsFromApi = await RefreshUrlsCore(urlsToRefresh.ToArray());

                for (int i = 0; i < urlsToRefresh.Count; i++)
                {
                    if (cache.Count >= maxCacheSize)
                    {
                        var oldestKey = cache.Keys.First();

                        cache.Remove(oldestKey);
                    }

                    var refreshedUrl = refreshedUrlsFromApi[i];

                    if (refreshedUrl.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase))
                    {
                        cache[urlsToRefresh[i]] = ('?' + refreshedUrl.Split('?')[1], DateTime.Now);
                    }

                    refreshedUrls.Add(refreshedUrl);
                }
            }

            return refreshedUrls.ToArray();
        }

        private static AuthenticationHeaderValue discordAuth = new AuthenticationHeaderValue("Bot", Environment.GetEnvironmentVariable("TOKEN") ?? throw new ArgumentNullException("Token environment variable is null"));

        private static async Task<string[]> RefreshUrlsCore(string[] urls)
        {
            if (urls.Length > 50) throw new ArgumentOutOfRangeException(nameof(urls), "Urls length cant be bigger than 50");

            var data = JsonSerializer.Serialize(new { attachment_urls = urls });

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://canary.discord.com/api/v9/attachments/refresh-urls")
            {
                Headers = { Authorization = discordAuth },
                Content = new StringContent(data, Encoding.UTF8, "application/json")
            })
            {
                using (HttpResponseMessage res = await Program.client.SendAsync(req))
                {
                    var str = await res.Content.ReadAsStringAsync();

                    return JsonNode.Parse(str)["refreshed_urls"].AsArray().Select(element => (string)element["refreshed"]).ToArray();
                }
            }
        }

        public static string EncodeAttachementName(ulong channelId, int index, int amount) => Base64Url.ToBase64Url(BitConverter.GetBytes((channelId) ^ (ulong)index ^ (ulong)amount)).TrimStart('_') + '_' + (amount - index);

        public static ulong[] DecompressArray(byte[] data) => ArrayDeserealizer(data);

        private static ulong[] ArrayDeserealizer(byte[] data)
        {
            using (MemoryStream memStr = new MemoryStream(data))
            {
                ulong deltaMin = memStr.ReadULong(false);

                ulong last = 0;

                ulong[] array = new ulong[(memStr.Length / sizeof(ulong)) - 1];

                for (int i = 0; i < array.Length; i++)
                {
                    ulong num = memStr.ReadULong(i % 2 == 0);

                    array[i] = num + last;

                    if (i != array.Length - 1) array[i] += deltaMin;

                    last = array[i];
                }

                return array;
            }
        }
    }
}