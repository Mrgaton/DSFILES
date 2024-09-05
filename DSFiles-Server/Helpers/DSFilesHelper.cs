using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSFiles_Server.Helpers
{
    internal class DSFilesHelper
    {
        private static byte[] XorKey = Properties.Resources.bin;

        public static byte[] U(ref byte[] data) => U(ref data, ref XorKey);

        public static byte[] U(ref byte[] data, ref byte[] key)
        {
            byte[] result = new byte[data.Length];
            int max = data.Length - 1;
            byte last = (byte)(data.Length % byte.MaxValue);

            for (int i = 0; i < data.Length; i++)
            {
                int keyIndex = i % key.Length;

                data[i] -= last;

                byte b = (byte)(data[i] ^ key[keyIndex]);

                result[max - i] = b;
                last += b;

                if (i % 2 == 0)
                {
                    last &= key[key.Length - keyIndex - 1];
                }
                else
                {
                    last ^= key[key.Length - keyIndex - 1];
                }
            }

            return result;
        }

        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>();

        private static readonly int maxCacheSize = 3 * 1000;

        public static async Task<string[]> RefreshUrls(string[] urls)
        {
            if (urls.Length > 50) throw new Exception("urls length cant be bigger than 50");

            List<string> refreshedUrls = new List<string>();
            List<string> urlsToRefresh = new List<string>();

            foreach (var url in urls)
            {
                if (cache.ContainsKey(url))
                {
                    refreshedUrls.Add(cache[url]);
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

                    cache[urlsToRefresh[i]] = refreshedUrlsFromApi[i];

                    refreshedUrls.Add(refreshedUrlsFromApi[i]);
                }
            }

            return refreshedUrls.ToArray();
        }

        private static async Task<string[]> RefreshUrlsCore(string[] urls)
        {
            if (urls.Length > 50) throw new Exception("urls length cant be bigger than 50");

            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, "https://gato.ovh/attachments/refresh-urls"))
            {
                var data = JsonSerializer.Serialize(new Dictionary<string, object>() { { "attachment_urls", urls } });

                message.Content = new StringContent(data);

                using (HttpResponseMessage response = await Program.client.SendAsync(message))
                {
                    var str = await response.Content.ReadAsStringAsync();

                    return JsonNode.Parse(str)["refreshed_urls"].AsArray().Select(element => (string)element["refreshed"]).ToArray();
                }
            }
        }

        public static string EncodeAttachementName(ulong channelId, ulong lastMessage, int index, int amount) => Base64Url.ToBase64Url(BitConverter.GetBytes(channelId - lastMessage ^ (ulong)index ^ (ulong)amount)).TrimStart('_') + '_' + (amount - index);

        public static ulong[] DecompressArray(byte[] data) => ArrayDeserealizer(data);

        private static ulong[] ArrayDeserealizer(byte[] data)
        {
            using (MemoryStream memStr = new MemoryStream(data))
            {
                ulong deltaMin = memStr.ReadULong(false);

                ulong last = 0;

                ulong[] array = new ulong[memStr.Length / sizeof(ulong) - 1];

                for (int i = 0; i < array.Length; i++)
                {
                    ulong num = memStr.ReadULong(i % 2 == 0);
                    array[i] = num + last + deltaMin;

                    last = array[i];
                }

                return array;
            }
        }
    }
}