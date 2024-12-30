using DSFiles_Server.Helpers;
using DSFiles_Shared;
using System.Net;

namespace DSFiles_Server.Routes
{
    internal class DSFilesUploadHandle
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
                if (httpStream.Length > 25 * 1000 * 1000)
                {
                    res.Send("File is too big");

                    return;
                }

                var result = await DiscordFilesSpliter.EncodeCore(new WebHookHelper(Program.client, webHook), fileName, httpStream);

                res.Send(result.ToJson());
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