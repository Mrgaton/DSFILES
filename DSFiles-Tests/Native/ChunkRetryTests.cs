using DSFiles_Server;
using DSFiles_Server.Routes;
using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text.Json;
using System.Text;

namespace DSFiles_Tests
{
    internal static class ChunkRetryTests
    {
        public static async Task Run()
        {
            var original = DSFiles_Server.Program.uploadClient;
            var handler = new FakeDiscord();
            DSFiles_Server.Program.uploadClient = new HttpClient(handler);
            try
            {
                int size = DiscordFilesSpliter.CHUNK_SIZE;
                var session = Context();
                session.Request.Headers["webhook"] = "https://discord.com/api/webhooks/123/test-token";
                session.Request.Headers["filename"] = "retry-test.bin";
                session.Request.Headers["filesize"] = (size + 8).ToString();
                session.Request.Headers["encrypt"] = "false";
                session.Request.Headers["Upload-Protocol"] = "2";
                await DSFilesChunkedUploadHandle.HandleHandshake(session.Request, session.Response);
                Require(session.Response.StatusCode == 200 && session.Response.Headers["Upload-Protocol"] == "2", "Retry capability");
                string id = session.Response.Headers["Session-ID"].ToString();
                byte[] first = new byte[size];
                var uploaded = await Chunk(id, 1, first);
                string firstReceipt = Receipt(uploaded);
                var changed = await Chunk(id, 1, new byte[1]);
                Require(changed.Response.StatusCode == 422 && handler.Posts == 1, "Wrong chunk size rejection");
                var lastChunk = await Chunk(id, 2, new byte[8]);
                string lastReceipt = Receipt(lastChunk);
                var final = await Finalize(id, [firstReceipt, lastReceipt]);
                var repeated = await Finalize(id, [firstReceipt, lastReceipt]);
                using (var firstReply = JsonDocument.Parse(Text(final)))
                using (var repeatedReply = JsonDocument.Parse(Text(repeated)))
                {
                    Require(firstReply.RootElement.GetProperty("seed").GetString() == repeatedReply.RootElement.GetProperty("seed").GetString()
                        && handler.Posts == 2, "Finalization recovery never reuploads chunks");
                }
                var invalid = await Finalize(id, [firstReceipt, firstReceipt]);
                Require(invalid.Response.StatusCode == 400 && handler.Posts == 2, "Duplicate manifest rejection");

                var failingSession = Context();
                failingSession.Request.Headers["webhook"] = "https://discord.com/api/webhooks/123/test-token";
                failingSession.Request.Headers["filename"] = "uncertain.bin";
                failingSession.Request.Headers["filesize"] = "8";
                failingSession.Request.Headers["Upload-Protocol"] = "2";
                await DSFilesChunkedUploadHandle.HandleHandshake(failingSession.Request, failingSession.Response);
                handler.Fail = true;
                string failingId = failingSession.Response.Headers["Session-ID"].ToString();
                var failed = await Chunk(failingId, 1, new byte[8]);
                Require(failed.Response.StatusCode == 502 && handler.Posts == 3, "Uncertain external operation is reported as failure");
                Console.WriteLine("PASS: resumable fragment receipts, repeatable finalization and invalid-manifest protection");
            }
            finally
            {
                DSFiles_Server.Program.uploadClient.Dispose();
                DSFiles_Server.Program.uploadClient = original;
            }
        }

        private static DefaultHttpContext Context()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            return context;
        }

        private static async Task<DefaultHttpContext> Chunk(string id, int index, byte[] bytes)
        {
            var context = Context();
            context.Request.Headers["Session-ID"] = id;
            context.Request.Headers["chunk"] = index.ToString();
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            await DSFilesChunkedUploadHandle.HandleChunk(context.Request, context.Response);
            return context;
        }

        private static string Receipt(DefaultHttpContext context)
        {
            using var document = JsonDocument.Parse(Text(context));
            return document.RootElement.GetProperty("receipt").GetString()!;
        }

        private static async Task<DefaultHttpContext> Finalize(string id, string[] receipts)
        {
            var context = Context();
            context.Request.Headers["Session-ID"] = id;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ChunkManifest(receipts), AppJsonContext.Default.ChunkManifest);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            await DSFilesChunkedUploadHandle.HandleFinalize(context.Request, context.Response);
            return context;
        }

        private static string Text(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message + " failed.");
        }

        private sealed class FakeDiscord : HttpMessageHandler
        {
            public int Posts { get; private set; }
            public bool Fail { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"channel_id\":\"1234\",\"guild_id\":\"1234\",\"id\":\"123\",\"name\":\"test\",\"type\":1,\"token\":\"test-token\"}")
                    });
                }
                Posts++;
                if (Fail) throw new HttpRequestException("Simulated lost external response");
                long id = 1234567890123456780L + Posts * 4194304L;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"" + id + "\",\"attachments\":[{\"id\":\"" + (id + 1) + "\"}]}")
                });
            }
        }
    }
}
