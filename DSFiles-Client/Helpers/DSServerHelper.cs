using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace DSFiles_Client.Helpers
{
    internal class DSServerHelper
    {
        public const string API_ENDPOINT = "https://gato.ovh/df";

        public static async Task AddFile(string json)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, API_ENDPOINT + "/file"))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                req.Headers.TryAddWithoutValidation("Cookie", $"token={Program.API_TOKEN}");

                using (var res = Program.client.SendAsync(req).Result)
                {
                    string response = res.Content.ReadAsStringAsync().Result;

                    await Program.DebugWriter.WriteLineAsync(response);
                    await Program.DebugWriter.WriteLineAsync();

                    JsonNode content = JsonNode.Parse(response);

                    Console.WriteLine("Uploaded to website as " + content["name"]);
                    Console.WriteLine();
                }
            }
        }

        public static async Task RemoveFile(string fileId)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Delete, API_ENDPOINT + "/file/" + fileId))
            {
                req.Headers.TryAddWithoutValidation("Cookie", $"token={Program.API_TOKEN}");

                using (var res = await Program.client.SendAsync(req))
                {
                    Console.WriteLine(res.Content.ReadAsStringAsync().Result);
                }
            }
        }
    }
}