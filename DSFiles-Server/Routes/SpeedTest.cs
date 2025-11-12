using DSFiles_Server.Helpers;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;

namespace DSFiles_Server.Routes
{
    internal static class SpeedTest
    {
        private static byte[] Header = Convert.FromBase64String("TVqQAAMAAAAEAAAA//8AALgAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+AAAAA4fug4AtAnNIbgBTM0hVGhpcyBwcm9ncmFtIGNhbm5vdCBiZSBydW4gaW4gRE9TIG1vZGUuDQ0KJAAAAAAAAAB6SD5EPilQFz4pUBc+KVAXN1HDFzopUBf8qFQWNilQF/yoUxY9KVAX/KhVFiopUBf8qFEWOClQF2ZcVBY6KVAXdVFRFj0pUBc+KVEXWClQF82rWRY/KVAXzatSFj8pUBdSaWNoPilQFwAAAAAAAAAAAAAAAAAAAABQRQAAZIYDAC56QmYAAAAAAAAAAPAAIiALAg4nAJAAAAAQAAAA0AAAMGMBAADgAAAAAACAAQAAAAAQAAAAAgAABgAAAAAAAAAGAAAAAAAAAACAAQAABAAAAAAAAAIAYAEAABAAAAAAAAAQAAAAAAAAAAAQAAAAAAAAEAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAcAEA4AEAAAAAAAAAAAAAADABAKQNAAAAAAAAAAAAAOBxAQAcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACoZQEAQAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABVUFgwAAAAAADQAAAAEAAAAAAAAAAEAAAAAAAAAAAAAAAAAACAAADgVVBYMQAAAAAAkAAAAOAAAACIAAAABAAAAAAAAAAAAAAAAAAAQAAA4FVQWDIAAAAAABAAAABwAQAAAgAAAIwAAAAAAAAAAAAAAAAAAEAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANC4yNABVUFghDSQCCPHrOp86dnpw");

        private static byte[] RandomBuffer = null;

        public static async Task HandleDownload(HttpRequest req, HttpResponse res)
        {
            if (RandomBuffer == null)
            {
                RandomBuffer = new byte[16 * 1024 * 1024];

                RandomNumberGenerator.Fill(RandomBuffer);
            }

            res.Headers["Cache-Control"] = ("no-cache, no-store, no-transform");
            res.Headers["Content-Length"] = ("9223372036854775807");
            res.Headers["Content-Disposition"] = ("attachment; filename=pato.exe");
            res.ContentType = "application/octet-stream";
            res.ContentLength = long.MaxValue;
            await res.Body.WriteAsync(Header);

            while (true)
            {
                await res.Body.WriteAsync(RandomBuffer);
            }
        }

        public static async Task HandleUpload(HttpRequest req, HttpResponse res)
        {
            byte[] buffer = new byte[8 * 1024 * 1024];

            int bytesRead = int.MaxValue;

            while (bytesRead > 0)
            {
                bytesRead = await req.Body.ReadAsync(buffer, 0, buffer.Length);
            }

            res.Headers["Cache-Control"] = ("no-cache, no-store, no-transform");
            await res.SendStatus(200);
        }
    }
}