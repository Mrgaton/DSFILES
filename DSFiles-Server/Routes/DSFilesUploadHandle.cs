using DSFiles_Server.Helpers;
using System.Net;

namespace DSFiles_Server.Routes
{
    internal class DSFilesUploadHandle
    {
        public static async Task HandleFile(HttpListenerRequest req, HttpListenerResponse res)
        {
            string? webHook = req.Headers.Get("webhook");

            if (webHook == null || string.IsNullOrWhiteSpace(webHook))
            {
                res.SendStatus(400, "Webhook header is missing");
                return;
            }
        }
    }
}