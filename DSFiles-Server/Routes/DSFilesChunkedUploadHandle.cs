using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DSFiles_Shared.DiscordFilesSpliter;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesChunkedUploadHandle
    {
        private const int MaximumChunks = 10000;
        private const int MaximumManifestBytes = 8 * 1024 * 1024;
        private static readonly SemaphoreSlim ActiveRequests = new(8);

        public static Task HandleHandshake(HttpRequest req, HttpResponse res) => Run(req, res, async () =>
        {
            if (!WebhookAddress.IsValid(req.Headers["webhook"].FirstOrDefault() ?? Environment.GetEnvironmentVariable("WEBHOOK")))
            {
                res.StatusCode = 400;
                await res.WriteAsync("A valid HTTPS Discord webhook is required.");
                return;
            }
            if (req.Headers["Upload-Protocol"] != "2")
            {
                res.StatusCode = 426;
                await res.WriteAsync("Chunk uploads require Upload-Protocol: 2. Update the client.");
                return;
            }
            try
            {
                JWTManager.EnsureDeletionKeys();
            }
            catch (InvalidOperationException)
            {
                res.StatusCode = 503;
                await res.WriteAsync("Chunk uploads require valid SIGNKEY and ENCRYPTKEY configuration.");
                return;
            }
            string name = req.Headers["filename"].ToString();
            if (name.Length is < 1 or > 64 || !long.TryParse(req.Headers["filesize"], out long size) || size <= 0)
            {
                res.StatusCode = 400;
                return;
            }
            if (size > (long)CHUNK_SIZE * MaximumChunks)
            {
                res.StatusCode = 413;
                return;
            }
            bool encrypt = true;
            if (req.Headers.TryGetValue("encrypt", out var value) && !bool.TryParse(value, out encrypt))
            {
                res.StatusCode = 400;
                return;
            }
            if (UploadProof.Consume(req, size) == false)
            {
                res.StatusCode = 403;
                return;
            }
            string url = req.Headers["webhook"].FirstOrDefault() ?? Environment.GetEnvironmentVariable("WEBHOOK")!;
            var hook = await WebHookHelper.CreateAsync(Program.uploadClient, url, req.HttpContext.RequestAborted);
            var expires = DateTimeOffset.UtcNow.AddHours(24);
            var session = new ChunkSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), name, size,
                encrypt ? RandomNumberGenerator.GetBytes(32) : null, url, hook.id, hook.token!, hook.channelId, expires.ToUnixTimeSeconds());
            res.Headers["Session-ID"] = JWTManager.CreateUploadSession(JsonSerializer.Serialize(session, AppJsonContext.Default.ChunkSession), expires);
            res.Headers["ChunkSize"] = CHUNK_SIZE.ToString();
            res.Headers["Upload-Protocol"] = "2";
            await res.WriteAsync("Upload authorized.", req.HttpContext.RequestAborted);
        });

        public static Task HandleChunk(HttpRequest req, HttpResponse res) => Run(req, res, async () =>
        {
            var session = ReadSession(req);
            if (!int.TryParse(req.Headers["chunk"], out int index) || index < 1 || index > Count(session))
            {
                res.StatusCode = 400;
                return;
            }
            long size = ChunkLength(session, index);
            if (req.ContentLength != size)
            {
                res.StatusCode = 422;
                return;
            }
            byte[] content = new byte[(int)size];
            await req.Body.ReadExactlyAsync(content, req.HttpContext.RequestAborted);
            using (var aes = new AesCTRStream(null, session.Key))
            {
                aes.Position = (long)(index - 1) * CHUNK_SIZE;
                aes.Encode(content, content.Length);
            }
            var hook = Hook(session);
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(content), "0", EncodeAttachementName(session.ChannelId, index - 1, Count(session)));
            string response = await hook.PostFileToWebhook(form, req.HttpContext.RequestAborted);
            var json = JsonNode.Parse(response);
            if (!ulong.TryParse(json?["attachments"]?[0]?["id"]?.ToString(), out ulong attachment)
                || !ulong.TryParse(json?["id"]?.ToString(), out ulong message) || attachment == 0 || message == 0)
                throw new HttpRequestException("Discord did not confirm the attachment.");
            var receipt = new ChunkReceipt(session.UploadId, index, attachment, message, size);
            string signed = JWTManager.CreateChunkReceipt(JsonSerializer.Serialize(receipt, AppJsonContext.Default.ChunkReceipt),
                DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt));
            await res.WriteAsJsonAsync(new ChunkResponse(signed), AppJsonContext.Default.ChunkResponse, cancellationToken: req.HttpContext.RequestAborted);
        }, uploading: true);

        public static Task HandleFinalize(HttpRequest req, HttpResponse res) => Run(req, res, async () =>
        {
            var session = ReadSession(req);
            var receipts = await ReadManifest(req, session, complete: true);
            using var seed = new MemoryStream();
            using var writer = new BinaryWriter(seed);
            writer.Write(new ByteConfig { Compression = false, VersionNumber = ByteConfig.ClassVersion }.ToByte());
            writer.Write(session.FileSize);
            writer.Write(session.ChannelId);
            writer.Write(new GorillaTimestampCompressor().Compress(receipts.Select(r => r.AttachmentId).ToArray()));
            var hook = Hook(session);
            // Finalization can be repeated on any replica without posting another Discord message.
            var upload = new Upload(session.FileName, seed.ToArray().Deflate(), session.Key,
                new GorillaTimestampCompressor().Compress(receipts.Select(r => r.MessageId).ToArray()), ref hook, postLog: false);
            res.ContentType = "application/json";
            await res.WriteAsync(UploadReceipt.Json(upload, protectCredential: true), req.HttpContext.RequestAborted);
        });

        public static Task HandleAbort(HttpRequest req, HttpResponse res) => Run(req, res, async () =>
        {
            var session = ReadSession(req);
            var receipts = await ReadManifest(req, session, complete: false);
            int removed = 0;
            foreach (var receipt in receipts)
            {
                for (int attempt = 0; ; attempt++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete,
                        $"https://discord.com/api/webhooks/{session.WebhookId}/{session.WebhookToken}/messages/{receipt.MessageId}");
                    using var response = await Program.uploadClient.SendAsync(request, req.HttpContext.RequestAborted);
                    if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                    {
                        removed++;
                        break;
                    }
                    if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 3)
                        response.EnsureSuccessStatusCode();
                    double seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1;
                    if (!double.IsFinite(seconds) || seconds < 0 || seconds > 300)
                        throw new HttpRequestException("Invalid retry delay.");
                    await Task.Delay(TimeSpan.FromSeconds(seconds), req.HttpContext.RequestAborted);
                }
            }
            await res.WriteAsJsonAsync(new RemovalResponse(removed, receipts.Length), AppJsonContext.Default.RemovalResponse, cancellationToken: req.HttpContext.RequestAborted);
        });

        private static ChunkSession ReadSession(HttpRequest req)
        {
            string token = req.Headers["Session-ID"].ToString();
            if (token.Length is < 1 or > 8192)
                throw new CryptographicException();
            var session = JsonSerializer.Deserialize(JWTManager.ReadUploadSession(token), AppJsonContext.Default.ChunkSession)
                ?? throw new CryptographicException();
            if (session.FileSize <= 0 || session.FileSize > (long)CHUNK_SIZE * MaximumChunks
                || session.UploadId is not { Length: 32 } || session.FileName is not { Length: > 0 and <= 64 }
                || (session.Key != null && session.Key.Length != 32) || session.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new CryptographicException();
            return session;
        }

        private static async Task<ChunkReceipt[]> ReadManifest(HttpRequest req, ChunkSession session, bool complete)
        {
            if (req.ContentLength > MaximumManifestBytes)
                throw new ManifestTooLargeException();
            using var buffer = new MemoryStream();
            byte[] block = new byte[8192];
            int count;
            while ((count = await req.Body.ReadAsync(block, req.HttpContext.RequestAborted)) > 0)
            {
                if (buffer.Length + count > MaximumManifestBytes)
                    throw new ManifestTooLargeException();
                buffer.Write(block, 0, count);
            }
            var manifest = JsonSerializer.Deserialize(buffer.ToArray(), AppJsonContext.Default.ChunkManifest);
            if (manifest?.Receipts == null || manifest.Receipts.Length > Count(session)
                || (complete && manifest.Receipts.Length != Count(session)))
                throw new InvalidDataException();
            var receipts = new SortedDictionary<int, ChunkReceipt>();
            var attachments = new HashSet<ulong>();
            var messages = new HashSet<ulong>();
            foreach (var token in manifest.Receipts)
            {
                if (token == null)
                    throw new InvalidDataException();
                var receipt = JsonSerializer.Deserialize(JWTManager.ReadChunkReceipt(token), AppJsonContext.Default.ChunkReceipt)
                    ?? throw new InvalidDataException();
                if (receipt.UploadId != session.UploadId || receipt.Index < 1 || receipt.Index > Count(session)
                    || receipt.Size != ChunkLength(session, receipt.Index) || receipt.AttachmentId == 0 || receipt.MessageId == 0
                    || !receipts.TryAdd(receipt.Index, receipt) || !attachments.Add(receipt.AttachmentId) || !messages.Add(receipt.MessageId))
                    throw new InvalidDataException();
            }
            return receipts.Values.ToArray();
        }

        private static int Count(ChunkSession session) => checked((int)((session.FileSize - 1) / CHUNK_SIZE + 1));
        private static long ChunkLength(ChunkSession session, int index) => Math.Min(CHUNK_SIZE, session.FileSize - (long)(index - 1) * CHUNK_SIZE);
        private static WebHookHelper Hook(ChunkSession session) => WebHookHelper.FromVerifiedState(Program.uploadClient,
            session.WebhookUrl, session.ChannelId, session.WebhookId, session.WebhookToken);

        private sealed class ManifestTooLargeException : Exception;

        private static async Task Run(HttpRequest req, HttpResponse res, Func<Task> action, bool uploading = false)
        {
            res.Headers.CacheControl = "no-store";
            if (!await ActiveRequests.WaitAsync(0, req.HttpContext.RequestAborted))
            {
                res.StatusCode = 429;
                return;
            }
            try
            {
                await action();
            }
            catch (ManifestTooLargeException) { res.StatusCode = 413; }
            catch (CryptographicException) { res.StatusCode = 410; await res.WriteAsync("Invalid or expired upload authorization."); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or EndOfStreamException)
            {
                res.StatusCode = 400;
                await res.WriteAsync("Invalid upload request or fragment receipts.");
            }
            catch (OperationCanceledException) when (req.HttpContext.RequestAborted.IsCancellationRequested) { }
            catch (OperationCanceledException)
            {
                if (uploading)
                    res.Headers["Upload-Retry"] = "blocked";
                res.StatusCode = 504;
                await res.WriteAsync("Storage confirmation timed out.");
            }
            catch (HttpRequestException)
            {
                if (uploading)
                    res.Headers["Upload-Retry"] = "blocked";
                res.StatusCode = 502;
                await res.WriteAsync("Discord did not confirm the operation.");
            }
            finally { ActiveRequests.Release(); }
        }
    }
}
