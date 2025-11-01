using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore.Routing.Template;
using System.Net;
using System.Security.Cryptography;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesUploadHandle
    {
        public static async Task HandleFile(HttpListenerRequest req, HttpListenerResponse res)
        {
            string? webHook = req.Headers.Get("webhook");
            string? fileName = req.Headers.Get("filename");

            if (webHook == null || fileName == null || string.IsNullOrWhiteSpace(webHook))
            {
                res.SendStatus(400, "Webhook or FileName header is missing");
                return;
            }

            using (var httpStream = new HttpStream(req))
            {
                if (httpStream.Length > 100 * 1000 * 1000)
                {
                    res.Send("File is too big");

                    return;
                }

                using (MemoryStream ms = new MemoryStream())
                using (StreamWriter tempIds = new StreamWriter(ms))
                {
                    try
                    {
                        byte[] key = RandomNumberGenerator.GetBytes(new Random().Next(10, 16));

                        var result = await DiscordFilesSpliter.EncodeCore(new WebHookHelper(Program.client, webHook),
                            name: fileName,
                            stream: httpStream,
                            level: System.IO.Compression.CompressionLevel.Optimal,
                            onTheFlyCompression: true,
                            encodeKey: key, 
                            tempIdsWriter: tempIds, 
                            disposeIdsWritter: false);

                        res.Send(result.ToJson());
                    }
                    catch (Exception ex) 
                    {
                        Console.Error.WriteLine(ex.ToString());

                        tempIds.Flush();
                        ms.Position = 0;

                        var ids = (await new StreamReader(ms).ReadToEndAsync()).Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => ulong.TryParse(l, out ulong id) ? id : 0).ToArray();

                        await new WebHookHelper(Program.client, webHook).RemoveMessages(ids);
                    }
                }
            }
        }

        public class HttpStream : Stream
        {
            private readonly Stream _innerStream;
            private readonly long _contentLength;

            public HttpStream(HttpListenerRequest request)
            {
                _innerStream = request.InputStream;

                if (long.TryParse(request.Headers["Content-Length"], out var length))
                {
                    _contentLength = length;
                }
                else
                {
                    throw new InvalidOperationException("Content-Length header is missing or invalid.");
                }
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

            public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

            public override void SetLength(long value) => _innerStream.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _innerStream.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}