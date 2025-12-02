using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using System.IO.Compression;
using System.Security.Cryptography;
using static DSFiles_Shared.DiscordFilesSpliter;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesUploadHandle
    {
        public static async Task HandleFile(HttpRequest req, HttpResponse res)
        {
            req.Headers.TryGetValue("webhook", out var webHook);

            if (webHook.Count <= 0)
            {
                webHook = Environment.GetEnvironmentVariable("WEBHOOK");
            }

            req.Headers.TryGetValue("filename", out var fileName);

            if (webHook.Count <= 0 || fileName.Count <= 0 || string.IsNullOrWhiteSpace(webHook))
            {
                res.SendStatus(400, "Webhook or FileName header is missing");
                return;
            }

            bool compress = false;
            req.Headers.TryGetValue("compress", out var compressStr);
            bool.TryParse(compressStr, out compress);

            using (var httpStream = new HttpStream(req))
            {
                if (httpStream.Length > 100 * 1000 * 1000)
                {
                    await res.WriteAsync("File is too big");
                    return;
                }

                using (MemoryStream ms = new MemoryStream())
                using (StreamWriter tempIds = new StreamWriter(ms))
                {
                    try
                    {
                        byte[] key = RandomNumberGenerator.GetBytes(new Random().Next(10, 16));
                        CompressionLevel compLevel = !compress? CompressionLevel.NoCompression : DiscordFilesSpliter.IsCompresable(Path.GetExtension(fileName), httpStream.Length) ? CompressionLevel.SmallestSize : CompressionLevel.NoCompression;

                        var result = await DiscordFilesSpliter.EncodeCore(new WebHookHelper(Program.client, webHook),
                            name: fileName,
                            stream: httpStream,
                            level: compLevel,
                            onTheFlyCompression: compress && compLevel != CompressionLevel.NoCompression,
                            encodeKey: key,
                            tempIdsWriter: tempIds,
                            disposeIdsWritter: false
                        );

                        await res.WriteAsync(JWTManager.JWTEnabled ? JWTManager.CreateSecureToken(result.Json) : result.Json);
                        await res.Body.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex.ToString());

                        tempIds.Flush();
                        ms.Position = 0;

                        var ids = (await new StreamReader(ms).ReadToEndAsync()).Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => ulong.TryParse(l, out ulong id) ? id : 0).ToArray();

                        await new WebHookHelper(Program.client, webHook).RemoveMessages(ids);

                        throw ex;
                    }
                }
            }
        }

        public class HttpStream : Stream
        {
            private readonly Stream _innerStream;
            private readonly long _contentLength;

            public HttpStream(HttpRequest request)
            {
                _innerStream = request.Body ?? throw new ArgumentNullException(nameof(request.Body));

                if (long.TryParse(request.Headers["Content-Length"], out var length))
                    _contentLength = length;
                else
                    throw new InvalidOperationException("Content-Length header is missing or invalid.");
            }

            public override long Length => _contentLength;
            public override bool CanRead => _innerStream.CanRead;
            public override bool CanSeek => _innerStream.CanSeek;
            public override bool CanWrite => _innerStream.CanWrite;

            public override long Position
            {
                get => _innerStream.Position;
                set => _innerStream.Position = value;
            }

            public override void Flush() => _innerStream.Flush();

            // Synchronous Read - deliberately not supported so callers must use async.
            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException("Synchronous Read is not supported. Use ReadAsync instead.");
            }

            // Async read forwarding
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _innerStream.ReadAsync(buffer, offset, count, cancellationToken);

            // If you're on .NET Standard 2.1 / .NET Core 3.0+:
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => _innerStream.ReadAsync(buffer, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

            public override void SetLength(long value) => _innerStream.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing) _innerStream.Dispose();
                base.Dispose(disposing);
            }

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
                => _innerStream.CopyToAsync(destination, bufferSize, cancellationToken);
        }
    }
}