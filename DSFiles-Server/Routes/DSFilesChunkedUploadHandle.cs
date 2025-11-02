using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Routing.Template;
using System;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using static DSFiles_Server.Routes.DSFilesUploadHandle;
using static DSFiles_Shared.DiscordFilesSpliter;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesChunkedUploadHandle
    {
        private static ConcurrentDictionary<Guid, UploadSession> Sessions = new();

        public class UploadSession
        {
            public WebHookHelper WebHook { get; set; }
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public long TotalWritted { get; set; }
            public DateTime LastUploaded { get; set; }
            public int TotalChunks { get; set; }
            public int ChunkNum { get; set; }
            public AesCTRStream AesStream { get; set; }
            public ulong[] AttachementsIDs { get; set; }
            public ulong[] MessagesIDs { get; set; }
        }

        public static async Task HandleHandshake(HttpListenerRequest req, HttpListenerResponse res)
        {
            string? turnstileToken = req.Headers.Get("turnstile");

            string? webHook = req.Headers.Get("webhook");
            string? fileName = req.Headers.Get("filename");
            string? fileSizeStr = req.Headers.Get("filesize");

            if (webHook == null || string.IsNullOrWhiteSpace(webHook) || webHook.Length > 256 || fileName == null || fileName.Length > 64 || fileSizeStr == null || !long.TryParse(fileSizeStr, out long fileSize) || fileSize <= 0)
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
                AesStream = aesctrs,
                LastUploaded = DateTime.UtcNow,

                TotalChunks = totalChunks,
                ChunkNum = 0,
                TotalWritted = 0,

                AttachementsIDs = new ulong[totalChunks],
                MessagesIDs = new ulong[totalChunks]
            };

            res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
            res.AddHeader("Session-ID", sessionId.ToString());
            res.SendStatus(200);
        }

        public static async Task HandleChunk(HttpListenerRequest req, HttpListenerResponse res)
        {
            string sessionString = req.Headers.Get("Session-ID");
           
            if (string.IsNullOrWhiteSpace(sessionString) || !Guid.TryParse(sessionString, out Guid sessionGuid) || !Sessions.TryGetValue(sessionGuid, out UploadSession session))
            {
                res.SendStatus(410, "410 Session not provided");
                return;
            }

            string chunkStr = req.Headers.Get("chunk");

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
                    res.Send("File is not exactly " + DiscordFilesSpliter.CHUNK_SIZE + " bytes");
                    return;
                }

                string attachementName = DiscordFilesSpliter.EncodeAttachementName(session.WebHook.channelId, chunkIndex, session.TotalChunks);

                byte[] content = new byte[httpStream.Length];
                await httpStream.ReadAsync(content, 0, content.Length);
                session.AesStream.Position = (chunkIndex + 1) * DiscordFilesSpliter.CHUNK_SIZE;
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
                                session.AesStream.GetSubKey(),
                                new GorillaTimestampCompressor().Compress(session.MessagesIDs),
                                ref webHook
                            );

                            res.SendStatus(200, uploaded.ToJson());
                            return;
                        }
                    }

                    session.ChunkNum++;
                    res.SendStatus(200, $"Chunk {session.ChunkNum}/{session.TotalChunks} uploaded 🥵");
                    return;
                }
            }
        }
    }
}