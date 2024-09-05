using System.Net;
using DSFiles_Server.Helpers;

namespace DSFiles_Server.Routes
{
    internal class RedirectHandler
    {
        public static async Task HandleRedirect(HttpListenerRequest req, HttpListenerResponse res)
        {
            string[] urlSplited = req.Url.AbsolutePath.Split('/');

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"https://jspaste.eu/api/v2/documents/{urlSplited[2]}/raw"))
            {
                using (HttpResponseMessage response = await Program.client.SendAsync(request))
                {
                    bool iframe = req.QueryString.HasKeys();

                    string text = await response.Content.ReadAsStringAsync();

                    string url = text.Split('\n').LastOrDefault(l => l.Contains("://"));

                    if (iframe)
                    {
                        res.ContentType = "text/html; charset=utf-8";
                        res.Send("<!doctypehtml><html lang=en><meta charset=UTF-8><meta content=\"width=device-width,initial-scale=1\"name=viewport><style>body,html{margin:0;padding:0;height:100%;overflow:hidden}iframe{width:100%;height:100%;border:none}</style><iframe src=" + url + "></iframe>");
                    }
                    else
                    {
                        res.Redirect(url);
                        res.Send("<script type=\"text/javascript\">window.location.replace(\"" + url + "\");</script>");
                    }
                }
            }
        }
    }
}