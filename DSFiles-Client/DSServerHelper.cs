using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DSFiles_Client
{
    internal class DSServerHelper
    {
        public const string API_ENDPOINT = "http://127.0.0.1:3000";
        public static async Task AddFile(string fileName, string downloadToken, string removeToken, string jspLink, ulong size)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, API_ENDPOINT +  "/file"))
            {
                req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>()
                {
                    { "name", fileName },
                    { "download_token", downloadToken },
                    { "remove_token", removeToken },
                    { "jspaste", jspLink },
                    { "size", size },
                }), Encoding.UTF8, "application/json");

                req.Headers.TryAddWithoutValidation("Cookie", $"token={Program.API_TOKEN}");

                using (var res = Program.client.SendAsync(req).Result)
                {
                    Console.WriteLine(res.Content.ReadAsStringAsync().Result);
                }
            }
        }
        public static async Task RemoveFile(string fileId)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Delete, API_ENDPOINT + "/file/" + fileId))
            {
                req.Headers.TryAddWithoutValidation("Cookie", $"token={Program.API_TOKEN}");

                using (var res = Program.client.SendAsync(req).Result)
                {
                    Console.WriteLine(res.Content.ReadAsStringAsync().Result);
                }
            }
        }
    }
}
