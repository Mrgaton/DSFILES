﻿#define RANGE_FULLFILE

using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore.StaticFiles;
using System.Buffers;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DSFiles_Server.Routes
{
    internal class DSFilesDownloadHandle
    {
        private static Dictionary<string, string> contentTypes = new Dictionary<string, string>()
        {
            {"mkv","video/matroska" },
            {"mk3d","video/matroska-3d" },
            {"mka","audio/matroska" },

            {"ogg","audio/ogg" },
            {"flac","audio/flac" },
            {"mov","video/quicktime" },
            {"webm","audio/webm" },
            {"adts","audio/aac" }
        };

        private static FileExtensionContentTypeProvider contentTypeProvider = CreateProvider();

        private static FileExtensionContentTypeProvider CreateProvider()
        {
            var provider = new FileExtensionContentTypeProvider();

            foreach (var type in contentTypes)
            {
                var key = type.Key[0] == '.' ? type.Key : '.' + type.Key;

                if (!provider.Mappings.ContainsKey(key))
                {
                    provider.Mappings.Add(key, type.Value);
                }
                else
                {
                    provider.Mappings[key] = type.Value;
                }

                //Console.WriteLine(type.Key + " | " + type.Value);
            }

            return provider;
        }

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
                    res.SendStatus(400); //Seed invalida
                    return;
                }

                string fileName = seedSpltied.Length > 2 ? Encoding.UTF8.GetString(Base64Url.FromBase64Url(seedSpltied[0]).BrotliDecompress()) : urlSplited[3];

                string[] seedData = seedSpltied[seedSpltied.Length - 1].Split('$');
                byte[] seed = Base64Url.FromBase64Url(seedData[0]).Inflate();
                byte[]? key = seedData.Length > 1 ? Base64Url.FromBase64Url(seedData[1]) : null;

                uint relativeLength = BitConverter.ToUInt32(seed, 1);

                //var etag = seed.Hash();

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

                    res.Send("This seed was made from a deprecated version please download the the correct client version, probably it is the oldest one\n\nHere is the compiled seed: " +
                        Encoding.UTF8.GetBytes(fileNameWithoutExt).BrotliCompress().ToBase64Url() + ':' + extension + ':' + seed.Deflate().ToBase64Url());
                    return;
                }

                ulong channelId = BitConverter.ToUInt64(seed, 1 + sizeof(uint));

                ulong[] ids = DSFilesHelper.DecompressArray(seed.Skip(1 + sizeof(uint) + sizeof(ulong)).ToArray());

                if (ids.Length <= 0)
                {
                    res.SendCatError(406); //IDs nulas???

                    return;
                }

                long contentLength = ((ids.Length - 1) * CHUNK_SIZE) + relativeLength;

                int i = 0;

                var attachments = ids.Select(id =>
                {
                    string url = $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{DSFilesHelper.EncodeAttachementName(channelId, i + 1, ids.Length)}";
                    i++;
                    return url;
                }).ToArray();

                //res.Send('[' + string.Join(", ",attachements)+ ']');

                if (ids.Length > 2)
                {
                    if (req.Headers.Get("user-agent").Contains("bot", StringComparison.InvariantCultureIgnoreCase))
                    {
                        res.ContentType = "text/html; charset=utf-8";
                        res.SendStatus(200, string.Join(Properties.Resources.BotsPage, req.Headers.Get("authority")));
                        return;
                    }

                    res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
                }
                else
                {
                    res.AddHeader("Cache-Control", "public, max-age=86300");
                }

                contentTypeProvider.TryGetContentType(fileName, out string? contentType);

                res.AddHeader("Content-Type", contentType ?? "application/octet-stream");
                res.AddHeader("Accept-Ranges", "bytes");
                //res.AddHeader("ETAG", etag.ToBase64Url());

                if (config.Compression) res.AddHeader("content-encoding", "br");

                if (fullFile) res.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");

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
                    if (config.Compression)
                    {
                        res.SendStatus(500, "The data content is compressed and cant send specific chunks");
                        return;
                    }

                    long rangeNum = long.Parse(string.Join("", range.Where(char.IsNumber)));
                    int chunk = (int)(rangeNum / CHUNK_SIZE);

                    var offset = (int)(rangeNum % CHUNK_SIZE);

                    long start = (chunk * CHUNK_SIZE) + offset;
                    long end = contentLength - 1;

                    res.ContentLength64 = end - start + 1;
                    res.AddHeader("Content-Range", $"bytes {start}-{end}/{contentLength}");
                    res.StatusCode = 206;
                    res.OutputStream.Write([], 0, 0);

                    await SendFullFile(res, key, attachments.Skip(chunk).ToArray(), chunk, offset);

                    return;
                }

                res.ContentLength64 = contentLength;

                //res.OutputStream.Write([], 0, 0);

                //Thread.Sleep(200);

                if (!res.OutputStream.CanWrite)
                {
                    res.Close();

                    return;
                }

                await SendFullFile(res, key, attachments, 0);
            }
            catch (Exception ex)
            {
                GC.Collect();

                try
                {
                    res.Headers.Remove(HttpRequestHeader.CacheControl);
                }
                catch { }
                finally
                {
                    res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
                }

                Program.WriteException(ref ex);

                res.SendStatus(500, ex.ToString());
            }
        }

        public const int RefreshUrlsChunkSize = 50;

        public const int MaxRetries = 3;

        private static async Task SendFullFile(HttpListenerResponse res, byte[]? key, string[] attachments, int startChunk, int offset = 0)
        {
            int part = 0, retry = 0;

            using (TransformStream ts = new TransformStream(res.OutputStream, key))
            {
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

                            string id = CleanUrl(refreshedUrls[e - part]);

                            Console.WriteLine("Downloading id " + id + ' ' + ((startChunk + e) + 1) + "/" + attachments.Length + (offset != 0 ? " to offset " + id : null));

                            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                            {
                                if (offset != 0 && e == 0)
                                {
                                    request.Headers.Range = new RangeHeaderValue(offset, null);
                                }

                                using (var response = await Program.client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                                {
                                    response.EnsureSuccessStatusCode();

                                    Console.WriteLine("Sending id " + id);
                                    //dataPart = await response.Content.ReadAsByteArrayAsync();

                                    if (offset != 0 && e == 0)
                                    {
                                        ts.Position = ((startChunk + e + part) * CHUNK_SIZE) + offset;
                                    }
                                    else
                                    {
                                        ts.Position = ((startChunk + e + part) * CHUNK_SIZE);
                                    }

                                    using (var dataStream = await response.Content.ReadAsStreamAsync())
                                    {
                                        byte[] buffer = new byte[81920];

                                        int bytesRead;

                                        while ((bytesRead = await dataStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                        {
                                            await ts.WriteAsync(buffer, 0, bytesRead);
                                        }
                                    }
                                }
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            Console.WriteLine(ex.Message);

                            if (ex.StatusCode == HttpStatusCode.NotFound)
                            {
                                res.Headers.Remove(HttpRequestHeader.CacheControl);
                                res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");

                                res.SendCatError(204);
                                res.Close();
                                return;
                            }
                        }
                        catch (HttpListenerException ex)
                        {
                            Console.WriteLine(ex.Message);

                            return;
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

        private static string CleanUrl(string uri) => uri.Split('/')[6].Split('?')[0];

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
    }
}