#define RANGE_FULLFILE

using DSFiles_Server.Helpers;
using Microsoft.AspNetCore.StaticFiles;
using System.Linq;
using System.Net;
using System.Text;

namespace DSFiles_Server.Routes
{
    internal class DSFilesHandle
    {
        private static HttpClient client = new HttpClient();
        private static FileExtensionContentTypeProvider contentTypeProvider = new FileExtensionContentTypeProvider();
        private const long CHUNK_SIZE = 25 * 1024 * 1024 - 256;

        public static async Task HandleFile(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                bool fullFile = req.QueryString.Get("file") != null;

                string[] urlSplited = req.Url.AbsolutePath.Split('/');
                string[] seedSpltied = urlSplited[2].Split(':');

                if (seedSpltied.Length! > 2 && urlSplited.Length != 3)
                {
                    res.SendStatus(400);
                    return;
                }

                string fileName = seedSpltied.Length > 2 ? Encoding.UTF8.GetString(Base64Url.FromBase64Url(seedSpltied[0]).BrotliDecompress()) : urlSplited[3];

                byte[] seed = Base64Url.FromBase64Url(seedSpltied[seedSpltied.Length - 1]).Inflate();

                bool compressed = seed[0] == 255;

                ulong channelId = BitConverter.ToUInt64(seed, 1);
                ulong contentLength = BitConverter.ToUInt64(seed, 1 + sizeof(ulong));
                ulong[] ids = DSFilesHelper.DecompressArray(seed.Skip(1 + sizeof(ulong) * 2).ToArray());

                int i = 0;

                var attachments = ids.Select(id =>
                {
                    string url = $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{DSFilesHelper.EncodeAttachementName(channelId, i > 0 ? ids[i - 1] : int.MaxValue, i + 1, ids.Length)}";
                    i++;
                    return url;
                }).ToArray();

                //res.Send('[' + string.Join(", ",attachements)+ ']');

                if (req.Headers.Get("user-agent").Contains("bot", StringComparison.InvariantCultureIgnoreCase) && ids.Length > 2)
                {
                    res.SendStatus(503);
                    return;
                }

                contentTypeProvider.TryGetContentType(fileName.Replace("mkv","mp4",StringComparison.InvariantCultureIgnoreCase), out string? contentType);

                res.AddHeader("Content-Type", contentType ?? "application/octet-stream");
                res.AddHeader("Accept-Ranges", "bytes");
                res.AddHeader("ETAG", seedSpltied[0]);

                if (ids.Length > 2)
                {
                    res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
                }
                else
                {
                    res.AddHeader("Cache-Control", "public, max-age=31536000");
                }

                if (compressed)
                {
                    res.AddHeader("content-encoding", "br");
                }

                if (fullFile)
                {
                    res.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                }

                if (!fullFile) res.Headers.Set(HttpResponseHeader.AcceptRanges, "bytes");

                string range = req.Headers.Get("range");

                /*StringBuilder sb = new StringBuilder();
                var copy = req.Headers;
                foreach (var h in req.Headers.AllKeys.Select(e => new KeyValuePair<string, string>(e, copy.Get(e))))
                {
                    sb.AppendLine(h.Key + ':' + h.Value);
                }
                res.Headers.Add("cosa", Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString())));*/

                if (!fullFile && range != null)
                {
                    if (compressed)
                    {
                        res.SendStatus(500, "The data content is compressed and cant send specific chunks");
                        return;
                    }

                    long rangeNum = long.Parse(string.Join("", range.Where(char.IsNumber)));

                    int chunk = (int)(rangeNum / CHUNK_SIZE);
#if !RANGE_FULLFILE
                    long start = chunk * CHUNK_SIZE;
                    long end = start + CHUNK_SIZE - 1;

                    if (end + 1 + CHUNK_SIZE > (long)contentLength)
                    {
                        end = (long)contentLength - 1;
                    }

                    res.ContentLength64 = end - start + 1;
                    res.AddHeader("Content-Range", $"bytes {start}-{end}/{contentLength}");
                    res.StatusCode = 206;
                    await res.OutputStream.FlushAsync();

                    await SendChunk(res, attachments, chunk);
#else
                    long start = chunk * CHUNK_SIZE;
                    long end = end = (long)contentLength - 1;

                    res.ContentLength64 = end - start + 1;
                    res.AddHeader("Content-Range", $"bytes {start}-{end}/{contentLength}");
                    res.StatusCode = 206;
                    await res.OutputStream.FlushAsync();

                    await SendFullFile(res, attachments.Skip(chunk).ToArray(), chunk);
#endif
                    return;
                }

                res.ContentLength64 = (long)contentLength;

                //Thread.Sleep(200);

                if (!res.OutputStream.CanWrite)
                {
                    res.Close();
                    return;
                }

                await SendFullFile(res, attachments,0);
            }
            catch (Exception ex)
            {
                res.SendStatus(500, ex.ToString());
            }
        }

        public const int RefreshUrlsChunkSize = 50;

        public const int MaxRetries = 3;

        private static async Task SendFullFile(HttpListenerResponse res, string[] attachments, int start)
        {
            int part = 0;
            int retry = 0;

            while (part < attachments.Length)
            {
                string[] refreshedUrls = await DSFilesHelper.RefreshUrls(attachments.Skip(part).Take(attachments.Length - part > 0 ? RefreshUrlsChunkSize : part - attachments.Length).ToArray());

                byte[] dataPart;

                for (int e = part; e < part + RefreshUrlsChunkSize && e < attachments.Length; e++)
                {
                    string url = refreshedUrls[e - part];

                rety:
                    dataPart = null;

                    try
                    {
                        Console.WriteLine("Downloading id " + CleanUrl(refreshedUrls[e - part]) + " " + ((start + e + part) + 1) + "/" + attachments.Length);

                        dataPart = await client.GetByteArrayAsync(url);
                    }
                    catch (HttpRequestException ex)
                    {
                        if (ex.StatusCode == HttpStatusCode.NotFound)
                        {
                            res.Headers.Clear();
                            res.Close();
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.WriteException(ref ex);

                        retry++;

                        if (retry > MaxRetries)
                        {
                            res.Send(ex.ToString());
                            return;
                        }

                        Thread.Sleep(1000);
                        goto rety;
                    }

                    var decoded = DSFilesHelper.U(ref dataPart);

                    res.OutputStream.Write([], 0, 0);

                    await res.OutputStream.WriteAsync(decoded, 0, dataPart.Length);
                }

                part += RefreshUrlsChunkSize;
            }

            await res.OutputStream.FlushAsync();

            res.Close();
        }

        /*private static async Task SendChunk(HttpListenerResponse res, string[] attachments, int chunk)
        {
            int retry = 0;

            int part = chunk / RefreshUrlsChunkSize * RefreshUrlsChunkSize;

            string[] refreshedUrls = await DSFilesHelper.RefreshUrls(attachments.Skip(part).Take(attachments.Length - part > 0 ? RefreshUrlsChunkSize : part - attachments.Length).ToArray());

            string attachement = refreshedUrls[chunk - part];

            byte[] dataPart = null;

        rety:

            try
            {
                Console.WriteLine("Downloading chunk " + CleanUrl(attachement) + " " + (chunk + 1) + "/" + attachments.Length);

                dataPart = await client.GetByteArrayAsync(attachement);
            }
            catch (Exception ex)
            {
                Program.WriteException(ref ex);

                retry++;

                if (retry > MaxRetries)
                {
                    res.Send(ex.ToString());
                    return;
                }

                Thread.Sleep(1000);
                goto rety;
            }

            var decoded = DSFilesHelper.U(ref dataPart);

            if (res.OutputStream.CanWrite)
            {
                await res.OutputStream.WriteAsync(decoded, 0, dataPart.Length);
                await res.OutputStream.FlushAsync();
            }

            res.Close();
        }*/

        private static string CleanUrl(string uri)
        {
            return uri.Split('/')[6].Split('?')[0];
        }
    }
}