#define RANGE_FULLFILE

using DSFiles_Client;
using DSFiles_Server.Helpers;
using Microsoft.AspNetCore.StaticFiles;
using System.Data;
using System.Net;
using System.Text;

namespace DSFiles_Server.Routes
{
    internal class DSFilesHandle
    {
        private static HttpClient client = new HttpClient();
        private static FileExtensionContentTypeProvider contentTypeProvider = new FileExtensionContentTypeProvider();
        private const long CHUNK_SIZE = 25 * 1024 * 1024 - 256;

        private static Dictionary<byte[], long> ContentLenghCache = [];

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

                var etag = seed.Hash();

                ByteConfig config;

                try
                {
                    config = new ByteConfig(seed[0]);

                    if (config.VersionNumber != 1)
                    {
                        throw new VersionNotFoundException();
                    }
                }
                catch
                {
                    string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                    string extension = fileName.Contains('.') ? Path.GetExtension(fileName).Trim('.') : "";

                    res.Send("This seed was made from an old version please go to https://github.com/Mrgaton/DSFILES and download the correct version probably is the oldest one\n\nHere is the compiled seed: " +
                        Encoding.UTF8.GetBytes(fileNameWithoutExt).BrotliCompress().ToBase64Url() + ':' + extension + ':' + seed.Deflate().ToBase64Url());
                    return;
                }

                ulong channelId = BitConverter.ToUInt64(seed, 1);

                //ulong contentLength = BitConverter.ToUInt64(seed, 1 + sizeof(ulong));

                ulong[] ids = DSFilesHelper.DecompressArray(seed.Skip(1 + sizeof(ulong) * 1).ToArray());

                int i = 0;

                var attachments = ids.Select(id =>
                {
                    string url = $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{DSFilesHelper.EncodeAttachementName(channelId, i + 1, ids.Length)}";
                    i++;
                    return url;
                }).ToArray();

                //res.Send('[' + string.Join(", ",attachements)+ ']');

                if (req.Headers.Get("user-agent").Contains("bot", StringComparison.InvariantCultureIgnoreCase) && ids.Length > 3)
                {
                    res.SendStatus(503);
                    return;
                }

                contentTypeProvider.TryGetContentType(fileName.Replace("mkv", "mp4", StringComparison.InvariantCultureIgnoreCase), out string? contentType);

                res.AddHeader("Content-Type", contentType ?? "application/octet-stream");
                res.AddHeader("Accept-Ranges", "bytes");
                res.AddHeader("ETAG", etag.ToBase64Url());

                if (ids.Length > 3)
                {
                    res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
                }
                else
                {
                    res.AddHeader("Cache-Control", "public, max-age=31536000");
                }

                if (config.Compression)
                {
                    res.AddHeader("content-encoding", "br");
                }

                if (fullFile)
                {
                    res.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                }

                if (!fullFile) res.Headers.Set(HttpResponseHeader.AcceptRanges, "bytes");

                //res.Headers.Set(HttpResponseHeader.Connection, "keep-alive");
                //res.KeepAlive = true;

                string range = req.Headers.Get("range");

                /*StringBuilder sb = new StringBuilder();
                var copy = req.Headers;
                foreach (var h in req.Headers.AllKeys.Select(e => new KeyValuePair<string, string>(e, copy.Get(e))))
                {
                    sb.AppendLine(h.Key + ':' + h.Value);
                }
                res.Headers.Add("cosa", Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString())));*/

                if (!ContentLenghCache.TryGetValue(etag, out long contentLength))
                {
                    var response = client.Send(new HttpRequestMessage(HttpMethod.Get, DSFilesHelper.RefreshUrls([attachments.Last()]).Result[0])).Content;
                    contentLength = (long)(((attachments.Length - 1) * CHUNK_SIZE) + response.Headers.ContentLength);
                    ContentLenghCache.Add(etag, contentLength);

                    if (ContentLenghCache.Count > 2000) ContentLenghCache.Clear();
                }

                if (!fullFile && range != null)
                {
                    if (config.Compression)
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
                    var offset = (int)(rangeNum % CHUNK_SIZE);

                    long start = (chunk * CHUNK_SIZE) + offset;
                    long end = contentLength - 1;

                    res.ContentLength64 = end - start + 1;
                    res.AddHeader("Content-Range", $"bytes {start}-{end}/{contentLength}");
                    res.StatusCode = 206;

                    await SendFullFile(res, attachments.Skip(chunk).ToArray(), chunk, offset);
#endif
                    return;
                }

                res.ContentLength64 = contentLength;

                //Thread.Sleep(200);

                if (!res.OutputStream.CanWrite)
                {
                    res.Close();
                    return;
                }

                await SendFullFile(res, attachments, 0);
            }
            catch (Exception ex)
            {
                GC.Collect();

                Program.WriteException(ref ex);

                res.SendStatus(500, ex.ToString());
            }
        }

        public const int RefreshUrlsChunkSize = 50;

        public const int MaxRetries = 3;

        private static async Task SendFullFile(HttpListenerResponse res, string[] attachments, int start, int offset = 0)
        {
            int part = 0;
            int retry = 0;

            using (TransformStream ts = new TransformStream(res.OutputStream))
            {
                byte[] dataPart = null;

                while (part < attachments.Length)
                {
                    string[] refreshedUrls = await DSFilesHelper.RefreshUrls(attachments.Skip(part).Take(attachments.Length - part > 0 ? RefreshUrlsChunkSize : part - attachments.Length).ToArray());

                    for (int e = part; e < part + RefreshUrlsChunkSize && e < attachments.Length; e++)
                    {
                        string url = refreshedUrls[e - part];

                    rety:

                        try
                        {
                            if (!res.OutputStream.CanWrite)
                            {
                                throw new IOException("Client disconnected.");
                            }

                            Console.WriteLine("Downloading id " + CleanUrl(refreshedUrls[e - part]) + " " + ((start + e) + 1) + "/" + attachments.Length);

                            dataPart = await client.GetByteArrayAsync(url);

                            /*using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,url))
                            using (HttpResponseMessage response = await client.SendAsync(request))
                            {
                                Stream ds = await response.Content.ReadAsStreamAsync();

                                if (offset != 0 && e == 0)
                                {
                                    var dataLength = response.Content.Headers.ContentLength - offset;

                                    //ds.Position = offset;
                                    //byte[] buffer = new byte[offset];
                                    //ds.Read(buffer, 0, buffer.Length);

                                    ts.Position = ((start + e + part) * CHUNK_SIZE);

                                    Console.WriteLine("Writing to index " + (ts.Position + offset));

                                    await ds.CopyToAsync(ts, offset, 8120);
                                    offset = 0;
                                }
                                else
                                {
                                    await ds.CopyToAsync(ts);

                                    //await task.WaitAsync(CancellationToken.None);
                                }
                            }*/
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

                            Thread.Sleep(500);
                            goto rety;
                        }

                        //res.OutputStream.Write([], 0, 0);

                        if (offset != 0 && e == 0)
                        {
                            var dataLength = dataPart.Length - offset;
                            ts.Position = ((start + e + part ) * CHUNK_SIZE);

                            Console.WriteLine("Writing to index " + (ts.Position + offset));

                            await ts.WriteAsync(dataPart, offset, dataLength);
                            offset = 0;
                        }
                        else
                        {
                            await ts.WriteAsync(dataPart, 0, dataPart.Length);

                            //await task.WaitAsync(CancellationToken.None);
                        }
                    }

                    part += RefreshUrlsChunkSize;
                }
            }

            await res.OutputStream.FlushAsync();

            res.Close();
        }

        private sealed class ByteConfig
        {
            public bool Compression { get; set; }
            public int VersionNumber { get; set; } = 1;

            public ByteConfig()
            { }

            public ByteConfig(byte header)
            {
                this.Compression = (header & 0b10000000) != 0;
                this.VersionNumber = header & 0b00001111;

                if (VersionNumber == 0 || VersionNumber == 15) throw new ArgumentException("Byte header has been made with an unsuported byte");
            }

            public byte ToByte()
            {
                byte result = 0;

                if (Compression) result |= 0b10000000;

                result |= (byte)(VersionNumber & 0b00001111);
                return result;
            }
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

        private static string CleanUrl(string uri) => uri.Split('/')[6].Split('?')[0];
    }
}