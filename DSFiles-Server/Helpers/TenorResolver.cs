using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace DSFiles_Server.Helpers
{
    internal class TenorResolver
    {
        public static string Resovlve(string url)
        {
            string html = Encoding.UTF8.GetString(Program.client.GetByteArrayAsync(url).Result);
            var scripts = html.Split(new string[] { "<script id=\"store-cache\" type=\"text/x-cache\" nonce=\"" }, StringSplitOptions.None);

            foreach (var el in scripts)
            {
                var json = ReadUltil(el, '>', '<').Last().Trim();

                if (json.StartsWith('{') && json.EndsWith('}'))
                {
                    JsonNode node = JsonNode.Parse(json);

                    var data = ((JsonObject)(node["gifs"]["byId"])).ElementAt(0).Value["results"][0]["media_formats"];

                    return (string)data["webp_transparent"]["url"];
                }
            }

            return null;
        }

        private static string[] ReadUltil(string data,params char[] chars)
        {
            List<string> result = new List<string>();
            StringBuilder sb = new StringBuilder();

            var splited = data.ToArray();

            int i = 0;

            foreach (var t in chars)
            {
                for (i = i;  i < data.Length; i++)
                {
                    char c = splited[i];

                    if (t == c)
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                        break;
                    }
                       
                    sb.Append(c);
                }

                i++;
            }

            return result.ToArray();
        }
    }
}
