using DSFiles_Server.Helpers;
using System.Net;

namespace DSFiles_Server.Routes
{
    internal static class SpeedTest
    {
        private static byte[] Header = Convert.FromBase64String("TVqQAAMAAAAEAAAA//8AALgAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+AAAAA4fug4AtAnNIbgBTM0hVGhpcyBwcm9ncmFtIGNhbm5vdCBiZSBydW4gaW4gRE9TIG1vZGUuDQ0KJAAAAAAAAAB6SD5EPilQFz4pUBc+KVAXN1HDFzopUBf8qFQWNilQF/yoUxY9KVAX/KhVFiopUBf8qFEWOClQF2ZcVBY6KVAXdVFRFj0pUBc+KVEXWClQF82rWRY/KVAXzatSFj8pUBdSaWNoPilQFwAAAAAAAAAAAAAAAAAAAABQRQAAZIYDAC56QmYAAAAAAAAAAPAAIiALAg4nAJAAAAAQAAAA0AAAMGMBAADgAAAAAACAAQAAAAAQAAAAAgAABgAAAAAAAAAGAAAAAAAAAACAAQAABAAAAAAAAAIAYAEAABAAAAAAAAAQAAAAAAAAAAAQAAAAAAAAEAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAcAEA4AEAAAAAAAAAAAAAADABAKQNAAAAAAAAAAAAAOBxAQAcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACoZQEAQAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABVUFgwAAAAAADQAAAAEAAAAAAAAAAEAAAAAAAAAAAAAAAAAACAAADgVVBYMQAAAAAAkAAAAOAAAACIAAAABAAAAAAAAAAAAAAAAAAAQAAA4FVQWDIAAAAAABAAAABwAQAAAgAAAIwAAAAAAAAAAAAAAAAAAEAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANC4yNABVUFghDSQCCPHrOp86dnpw");

        private static Random random = new Random();

        private static byte[] RandomBuffer = null;

        public static void HandleDownload(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (RandomBuffer == null)
            {
                RandomBuffer = new byte[16 * 1024 * 1024];
                //random.NextBytes(RandomBuffer);
            }

            res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
            res.AddHeader("Content-Length", "9223372036854775807");
            res.AddHeader("Content-Disposition", "attachment; filename=pato.exe");
            res.ContentType = "application/octet-stream";
            res.ContentLength64 = long.MaxValue;
            res.OutputStream.Write(Header);

            while (true)
            {
                res.OutputStream.Write(RandomBuffer);
            }
        }

        public static void HandleUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            byte[] buffer = new byte[8 * 1024 * 1024];

            int bytesRead = int.MaxValue;

            while (bytesRead > 0)
            {
                bytesRead = req.InputStream.Read(buffer, 0, buffer.Length);
            }

            res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
            res.SendStatus(200);
        }
    }
}