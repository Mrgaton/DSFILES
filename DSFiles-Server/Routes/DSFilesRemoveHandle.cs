using DSFiles_Server.Helpers;
using DSFiles_Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesRemoveHandle
    {
        public static async Task HandleFile(HttpListenerRequest req, HttpListenerResponse res)
        {
            var token = req.RawUrl.Split('/').Last();

            var splited = token.Split(':');

            if (splited.Any(c => c.Contains('$')))
            {
                res.SendStatus(422, "422 The seed is not a remove token");
                return;
            }

            var webHookHelper = new WebHookHelper(Program.client, BitConverter.ToUInt64(splited[1].FromBase64Url()), splited[2]);
            ulong[] ids = new DiscordFilesSpliter.GorillaTimestampCompressor().Decompress(splited[0].FromBase64Url());

            StringBuilder sb = new StringBuilder();

            var progress = new Progress<string>((s) =>
            {
                sb.AppendLine(s);
            });

            await webHookHelper.RemoveMessages(ids, progress);

            res.Send(sb.ToString());
        }
    }
}
