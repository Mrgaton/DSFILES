using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static DSFiles_Server.Routes.DSFilesUploadHandle;
using static DSFiles_Shared.DiscordFilesSpliter;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesChunkedUploadHandle
    {
        private static ConcurrentDictionary<Guid, UploadSession> Sessions = new();

        static DSFilesChunkedUploadHandle()
        {
            Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var session in Sessions.ToArray())
                        {
                            if ((DateTime.UtcNow - session.Value.LastUploaded).TotalSeconds > 20)
                            {
                                Task.Factory.StartNew(() => session.Value.WebHook.RemoveMessages(session.Value.MessagesIDs));

                                Sessions.TryRemove(session.Key, out _);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex.ToString());
                    }

                    Thread.Sleep(20000);
                }
            });
        }

        public class UploadSession
        {
            public WebHookHelper WebHook { get; set; }
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public long TotalWritted { get; set; }
            public DateTime LastUploaded { get; set; }
            public int TotalChunks { get; set; }
            public int ChunkNum { get; set; }
            public byte[] Key { get; set; }
            public AesCTRStream AesStream { get; set; }
            public ulong[] AttachementsIDs { get; set; }
            public ulong[] MessagesIDs { get; set; }
        }

        public static async Task HandleHandshake(HttpRequest req, HttpResponse res)
        {
            req.Headers.TryGetValue("turnstile", out var turnstile);

            if (!req.Headers.TryGetValue("webhook", out var webHook))
            {
                webHook = Environment.GetEnvironmentVariable("WEBHOOK");
            }

            req.Headers.TryGetValue("filename", out var fileName);
            req.Headers.TryGetValue("filesize", out var fileSizeStr);

            if (webHook.Count <= 0 || string.IsNullOrWhiteSpace(webHook) || ((string)webHook).Length > 256 || fileName.Count <= 0 || ((string)fileName).Length > 64 || fileSizeStr.Count <= 0 || !long.TryParse(fileSizeStr, out long fileSize) || fileSize <= 0)
            {
                res.SendStatus(400, "Webhook or FileName or FileSize header is missing or invalid");
                return;
            }

            Guid sessionId = Guid.NewGuid();

            byte[] key = RandomNumberGenerator.GetBytes(new Random().Next(16, 20));
            AesCTRStream aesctrs = new AesCTRStream(null, key);

            var totalChunks = (int)(fileSize / DiscordFilesSpliter.CHUNK_SIZE) + 1;

            Sessions[sessionId] = new UploadSession()
            {
                WebHook = new WebHookHelper(Program.client, webHook),
                FileName = fileName,
                FileSize = fileSize,
                Key = key,
                AesStream = aesctrs,
                LastUploaded = DateTime.UtcNow,

                TotalChunks = totalChunks,
                ChunkNum = 0,
                TotalWritted = 0,

                AttachementsIDs = new ulong[totalChunks],
                MessagesIDs = new ulong[totalChunks]
            };

            res.Headers["Cache-Control"] = ("no-cache, no-store, no-transform");
            res.Headers["Session-ID"] = (sessionId.ToString());
            res.Headers["ChunkSize"] = (DiscordFilesSpliter.CHUNK_SIZE.ToString());
            res.SendStatus(200, "Happy happy happy 😊");
        }

        public static async Task HandleChunk(HttpRequest req, HttpResponse res)
        {
            req.Headers.TryGetValue("Session-ID", out var sessionString);

            if (string.IsNullOrWhiteSpace(sessionString) || !Guid.TryParse(sessionString, out Guid sessionGuid) || !Sessions.TryGetValue(sessionGuid, out UploadSession session))
            {
                res.SendStatus(410, "410 Session not provided");
                return;
            }

            req.Headers.TryGetValue("chunk", out var chunkStr);

            if (!int.TryParse(chunkStr, out int chunkIndex) || chunkIndex <= 0)
            {
                res.SendStatus(400, "400 Chunk index is missing or invalid");
                return;
            }

            chunkIndex -= 1;

            if (chunkIndex != session.ChunkNum)
            {
                res.SendStatus(409, "409 Chunk index is invalid");
                return;
            }

            session.LastUploaded = DateTime.UtcNow;

            using (var httpStream = new HttpStream(req))
            {
                if (session.TotalChunks != session.ChunkNum + 1 && httpStream.Length != DiscordFilesSpliter.CHUNK_SIZE)
                {
                    res.SendStatus(422, "File is not exactly " + DiscordFilesSpliter.CHUNK_SIZE + " bytes");
                    return;
                }

                string attachementName = DiscordFilesSpliter.EncodeAttachementName(session.WebHook.channelId, chunkIndex, session.TotalChunks);

                byte[] content = new byte[httpStream.Length];
                int readed = 0, totalReaded = 0;

                while ((readed = await httpStream.ReadAsync(content, totalReaded, content.Length - totalReaded)) > 0)
                {
                    totalReaded += readed;
                }

                int aesBasePosition = (chunkIndex) * DiscordFilesSpliter.CHUNK_SIZE;
                session.AesStream.Position = aesBasePosition;
                session.AesStream.Encode(content, content.Length);

                using (var sc = new ByteArrayContent(content))
                {
                    JsonNode? response = JsonNode.Parse(await session.WebHook.PostFileToWebhook([new WebHookHelper.FileData()
                    {
                        FileName = attachementName,
                        Data = sc
                    }]));

                    ulong attachementId = ulong.Parse((string)response["attachments"][0]["id"]);
                    ulong messageId = ulong.Parse((string)response["id"]);

                    if (attachementId <= 0 || messageId <= 0)
                        throw new InvalidDataException("Failed to upload the chunk and retrieve the attachment");

                    session.MessagesIDs[chunkIndex] = messageId;
                    session.AttachementsIDs[chunkIndex] = attachementId;
                    session.TotalWritted += content.Length;

                    if (session.ChunkNum + 1 == session.TotalChunks)
                    {
                        using (MemoryStream seedData = new MemoryStream())
                        using (BinaryWriter bw = new BinaryWriter(seedData))
                        {
                            ByteConfig config = new ByteConfig() { Compression = false, VersionNumber = 2 };

                            bw.Write(config.ToByte());
                            bw.Write(session.FileSize);
                            bw.Write(session.WebHook.channelId);
                            bw.Write(new GorillaTimestampCompressor().Compress(session.AttachementsIDs.ToArray()));

                            var webHook = session.WebHook;

                            var uploaded = new Upload(
                                session.FileName,
                                seedData.ToArray().Deflate(),
                                session.Key,
                                new GorillaTimestampCompressor().Compress(session.MessagesIDs),
                                ref webHook
                            );

                            await res.WriteAsync(uploaded.Json);
                            Sessions.TryRemove(sessionGuid, out _);
                            return;
                        }
                    }

                    session.ChunkNum++;
                    await res.SendStatus(200, $"Chunk {session.ChunkNum}/{session.TotalChunks} uploaded 🥵");
                }
            }
        }
    }
}