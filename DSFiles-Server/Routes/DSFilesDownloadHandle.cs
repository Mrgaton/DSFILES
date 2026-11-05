#define RANGE_FULLFILE

using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Primitives;
using System.Buffers;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using static DSFiles_Shared.DiscordFilesSpliter;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesDownloadHandle
    {
        private static Dictionary<string, string> contentTypes = new Dictionary<string, string>()
        {
            { "mkv", "video/matroska" },
            { "wav", "audio/x-wav" },
            { "mk3d", "video/matroska-3d" },
            { "mka", "audio/matroska" },

            { "ogg", "audio/ogg" },
            { "oga", "audio/ogg" },
            { "flac", "audio/flac" },
            { "mov", "video/quicktime" },
            { "webm", "audio/webm" },
            { "adts", "audio/aac" },

            { "opus", "audio/opus" },
            { "avif", "image/avif" },
            { "heic", "image/heic" },

            { "srt",  "application/x-subrip" },
            { "ass",  "text/x-ssa" }
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

                Console.WriteLine("Replacing content type: " + type.Key + " | " + type.Value);
            }

            return provider;
        }

        public static async Task HandleFile(HttpRequest req, HttpResponse res, CancellationToken token)
        {

            // Do not add allow-same-origin: uploaded scripts must remain isolated from the app.
            res.Headers["Content-Security-Policy"] = "sandbox allow-scripts";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";

            try
            {
                bool fullFile = req.Query["file"].Count > 0;

                string[] urlSplited = req.Path.ToString().Split('/');
                string[] seedSpltied = urlSplited[2].Split(':');

                if (seedSpltied.Length! > 2 && urlSplited.Length != 3)
                {
                    res.StatusCode = 400;
                    return;
                }

                string fileName = seedSpltied.Length > 2 ? Encoding.UTF8.GetString(seedSpltied[0].FromBase64Url().BrotliDecompress(maximumOutputBytes: 4096)) : urlSplited[urlSplited.Length - 1];

                if (urlSplited.Length > 4)
                {
                    fullFile = urlSplited[3] == "f";
                }

                string[] seedData = seedSpltied[seedSpltied.Length - 1].Split('$');
                byte[] seed = seedData[0].FromBase64Url().Inflate(maximumOutputBytes: 256 * 1024);
                byte[]? key = seedData.Length > 1 ? seedData[1].FromBase64Url() : null;

                ByteConfig config;

                int currentVersion = ByteConfig.ClassVersion;

                try
                {
                    config = new ByteConfig(seed[0]);

                    if (config.VersionNumber > ByteConfig.ClassVersion)
                    {
                        res.StatusCode = 405;
                        await res.WriteAsync(($"This seed was made with a newer version ({config.VersionNumber}) of the client, nor you are from the feature, this server is outdated or the seed is gibberish"));
                        return;
                    }
                    else if (config.VersionNumber < ByteConfig.ClassVersion)
                    {
                        res.StatusCode = 405;
                        await res.WriteAsync(($"This seed was made with an older version (Client {config.VersionNumber}) of the client, download the file from the correct client version."));
                        return;
                    }
                    else if (config.VersionNumber != currentVersion)
                    {
                        throw new VersionNotFoundException();
                    }
                }
                catch
                {
                    string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                    string extension = fileName.Contains('.') ? Path.GetExtension(fileName).Trim('.') : "";

                    await res.WriteAsync("This seed was made from a deprecated version (probably the first one) please download the the correct client version, probably it is the oldest one\n\nHere is the compiled seed: " +
                        Encoding.UTF8.GetBytes(fileNameWithoutExt).BrotliCompress().ToBase64Url() + ':' + extension + ':' + seed.Deflate().ToBase64Url());
                    return;
                }

                long contentLength = BitConverter.ToInt64(seed, 1);

                ulong channelId = BitConverter.ToUInt64(seed, 1 + sizeof(ulong));

                ulong[] ids = new GorillaTimestampCompressor().Decompress(seed.Skip(1 + sizeof(long) + sizeof(ulong)).ToArray());

                if (ids.Length is < 1 or > 10000 || contentLength < 0 || contentLength > (long)CHUNK_SIZE * ids.Length)
                {
                    res.StatusCode = 400;
                    return;
                }

                // Legacy uploads include an empty final part at exact boundaries;
                // chunked uploads do not. The seed records the actual part count.
                // Compressed uploads retain their historical naming convention.
                int attachmentNameCount = config.Compression ? (int)(contentLength / CHUNK_SIZE) + 1 : ids.Length;
                string[] attachments = ids.Select((id, index) => $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{EncodeAttachementName(channelId, index, attachmentNameCount)}").ToArray();

                if (req.Query.ContainsKey("redirect"))
                {
                    await RedirectHandler.HandleFileRedirect(attachments, contentLength, key, config.Compression, res, token);
                    return;
                }

                // Refresh up to 50 in one request; playback and seeks reuse the cached URLs.
                var refreshed = await DSFilesHelper.RefreshUrls(attachments[Math.Max(0, attachments.Length - RefreshUrlsChunkSize)..]);

                if (config.Compression)
                {
                    var lastSize = await DSFilesHelper.GetAttachmentSize(refreshed.Last());

                    contentLength = ((attachments.Length - 1) * (long)CHUNK_SIZE) + lastSize;
                }

                if (ids.Length > 5)
                {
                    res.Headers["Cache-Control"] = ("no-cache, no-store, no-transform");

                    if (!req.Headers.TryGetValue("user-agent", out StringValues userAgent) || userAgent.ToString().Contains("bot", StringComparison.InvariantCultureIgnoreCase))
                    {
                        req.Headers.TryGetValue("authority", out var authority);
                        res.ContentType = "text/html; charset=utf-8";
                        res.SendStatus(200, string.Join(Properties.Resources.BotsPage, authority.ToString()));
                        return;
                    }
                }
                else
                {
                    res.Headers["Cache-Control"] = ("public, max-age=31536000, immutable");
                }

                contentTypeProvider.TryGetContentType(fileName, out string? contentType);

                res.Headers["Content-Type"] = (contentType ?? "application/octet-stream");
                res.Headers["Accept-Ranges"] = ("bytes");

                if (config.Compression)
                    res.Headers["content-encoding"] = ("br");

                if (fullFile)
                    res.Headers["Content-Disposition"] = ($"attachment; filename=\"{fileName}\"");

                if (!fullFile)
                    res.Headers.AcceptRanges = "bytes";

                string range = req.Headers.Range;

                if (!fullFile && range != null)
                {
                    if (config.Compression)
                    {
                        res.SendStatus(416, "The data content is compressed and cant send specific range");
                        return;
                    }
                    var rangeStr = string.Join("", range.Split('-')[0].Where(char.IsNumber));

                    if (!long.TryParse(rangeStr, out long rangeNum) || rangeNum >= contentLength)
                    {
                        res.StatusCode = 416;
                        res.Headers["Content-Range"] = $"bytes */{contentLength}";
                        return;
                    }

                    int chunk = (int)(rangeNum / CHUNK_SIZE);

                    var offset = (int)(rangeNum % CHUNK_SIZE);

                    long start = ((long)chunk * CHUNK_SIZE) + offset;
                    long end = contentLength - 1;

                    res.ContentLength = end - start + 1;
                    res.Headers["Content-Range"] = ($"bytes {start}-{end}/{contentLength}");
                    res.StatusCode = 206;
                    res.BodyWriter.Write([]);

                    await SendFullFile(res, key, attachments.Skip(chunk).ToArray(), chunk, offset, token);
                    return;
                }

                res.ContentLength = contentLength;

                await SendFullFile(res, key, attachments, 0, 0, token);
            }
            catch (Exception ex)
            {
                try
                {
                    res.Headers["Cache-Control"] = "no-cache, no-store, no-transform";
                }
                catch { }
                finally
                {

                }

                Console.Error.WriteLine(ex);
                if (res.HasStarted)
                {
                    req.HttpContext.Abort();
                    return;
                }
                if (token.IsCancellationRequested)
                    return;
                res.ContentType = "text/plain";
                res.StatusCode = ex is FormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException ? 400 : 502;
                await res.WriteAsync("Could not process the download request.", token);
            }
        }

        public const int RefreshUrlsChunkSize = 50;

        public const int MaxRetries = 3;

        private static async Task SendFullFile(HttpResponse res, byte[]? key, string[] attachments, int startChunk, int offset = 0, CancellationToken ct = default)
        {
            int part = 0;
            var stream = res.BodyWriter.AsStream();

            using (AesCTRStream ts = new AesCTRStream(stream, key))
            {
                while (part < attachments.Length && !ct.IsCancellationRequested)
                {
                    try
                    {

                        string[] refreshedUrls = await DSFilesHelper.RefreshUrls(attachments.Skip(part).Take(attachments.Length - part > 0 ? RefreshUrlsChunkSize : part - attachments.Length).ToArray());

                        for (int e = part; e < part + RefreshUrlsChunkSize && e < attachments.Length; e++)
                        {
                            if (ct.IsCancellationRequested)
                                break;

                            string url = refreshedUrls[e - part];
                            string id = CleanUrl(refreshedUrls[e - part]);

                            Console.WriteLine("Downloading id " + id + ' ' + ((startChunk + e) + 1) + "/" + attachments.Length + (offset != 0 ? " to offset " + id : null));

                            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                            {
                                if (offset != 0 && e == 0)
                                {
                                    request.Headers.Range = new RangeHeaderValue(offset, null);
                                }

                                using (var response = await Program.client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                                {
                                    response.EnsureSuccessStatusCode();

                                    if (e + 1 != attachments.Length && response.Content.Headers.ContentLength != CHUNK_SIZE)
                                    {
                                        throw new InvalidDataException("Chunk size is not consistent");
                                    }

                                    if (offset != 0 && e == 0)
                                    {
                                        if (offset > CHUNK_SIZE)
                                            throw new InvalidDataException("Something terribly terrible happened with the offset.");

                                        ts.Position = ((long)(startChunk + e) * CHUNK_SIZE) + offset;
                                    }
                                    else
                                    {
                                        ts.Position = (long)(startChunk + e) * CHUNK_SIZE;
                                    }

                                    using (var dataStream = await response.Content.ReadAsStreamAsync(ct))
                                    {
                                        byte[] buffer = new byte[81920];

                                        int bytesRead;

                                        while ((bytesRead = await dataStream.ReadAsync(buffer, ct)) > 0)
                                        {
                                            await ts.WriteAsync(buffer, 0, bytesRead, ct);

                                        }

                                    }
                                }
                            }
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.Error.WriteLine(ex.Message);

                        if (ex.StatusCode == HttpStatusCode.NotFound)
                        {
                            res.Headers.CacheControl = "";
                            res.Headers["Cache-Control"] = ("no-cache, no-store, no-transform");
                            await res.SendCatError(204);
                            return;
                        }
                        else
                        {
                            if (res.HasStarted)
                            {
                                res.HttpContext.Abort();
                                return;
                            }
                            res.StatusCode = 502;
                            await res.WriteAsync("Could not retrieve the file.", ct);
                            return;
                        }
                    }
                    catch (HttpListenerException ex)
                    {
                        await Console.Error.WriteLineAsync(ex.Message);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex);
                        if (res.HasStarted)
                        {
                            res.HttpContext.Abort();
                            return;
                        }
                        res.StatusCode = 502;
                        await res.WriteAsync("Could not retrieve the file.", ct);
                        return;
                    }

                    part += RefreshUrlsChunkSize;
                }
            }
        }

        private static string CleanUrl(string uri) => uri.Split('/')[6].Split('?')[0];

    }
}
