using Microsoft.AspNetCore.Http;
using System.Web;

namespace DSFiles_Server.Routes
{
    internal static class RedirectHandler
    {
        public static async Task HandleRedirect(HttpRequest req, HttpResponse res)
        {
            string[] urlSplited = req.Path.ToString().Split('/');

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"https://jspaste.eu/api/v2/documents/{urlSplited[2]}/raw"))
            {
                using (HttpResponseMessage response = await Program.client.SendAsync(request))
                {
                    bool iframe = req.Query.Any();

                    string text = await response.Content.ReadAsStringAsync();

                    string url = HttpUtility.UrlEncode(text.Split('\n').LastOrDefault(l => l.Contains("://")));

                    if (iframe)
                    {
                        res.ContentType = "text/html; charset=utf-8";
                        await res.WriteAsync("<!doctypehtml><html lang=en><meta charset=UTF-8><meta content=\"width=device-width,initial-scale=1\"name=viewport><style>body,html{margin:0;padding:0;height:100%;overflow:hidden}iframe{width:100%;height:100%;border:none}</style><iframe src=" + url + "></iframe>");
                    }
                    else
                    {
                        res.Redirect(url);
                        await res.WriteAsync("<script type=\"text/javascript\">window.location.replace(\"" + url + "\");</script>");
                    }
                }
            }
        }
    }
}