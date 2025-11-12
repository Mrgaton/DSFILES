using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using static AnsiHelper;

namespace DSFiles_Shared
{
    public static class DiscordFilesSpliter
    {
        private static HttpClient client = new HttpClient();

        public const string UnsendedIds = "Missing.dat";
        public static StreamWriter UnsendedIdsWriter { get => new StreamWriter(File.Open(UnsendedIds, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { AutoFlush = true }; }

        /*public static IEnumerable<byte[]> SplitByLength(byte[] bytes, int maxLength)
        {
            for (int index = 0; index < bytes.Length; index += maxLength)
            {
                yield return bytes.Skip(index).Take(Math.Min(maxLength, bytes.Length - index)).ToArray();
            }
        }*/

        //private const long CHUNK_SIZE = (25 * 1024 * 1024) - 256;

        public static IProgress<string> ConsoleProgress = new Progress<string>(Console.WriteLine);
        public static IProgress<string> ErrorProgress = new Progress<string>(Console.Error.WriteLine);

        public const int CHUNK_SIZE = (10 * 1000 * 1000) - 256;
        private const int IOBuffer = 128 * 1024;

        private const int MaxTimeListBuffer = 10;

        private static List<long> timeList = new List<long>();

        public static async Task<string[]> RefreshUrls(string[] urls)
        {
            if (urls.Length > 50) throw new Exception("urls length cant be bigger than 50");

            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, "https://gato.ovh/attachments/refresh-urls"))
            {
                var data = JsonSerializer.Serialize(new Dictionary<string, object>() { { "attachment_urls", urls } });

                message.Content = new StringContent(data);

                using (HttpResponseMessage response = await client.SendAsync(message))
                {
                    var str = await response.Content.ReadAsStringAsync();

                    //Console.WriteLine(str);

                    return JsonNode.Parse(str)["refreshed_urls"].AsArray().Select(element => (string)element["refreshed"]).ToArray();
                }
            }
        }

        public static string EncodeAttachementName(ulong channelId, int index, int amount) => (BitConverter.GetBytes((channelId) ^ (ulong)index ^ (ulong)amount)).ToBase64Url().TrimStart('_') + '_' + (amount - index);

        private static readonly string[] NonCompresableExt = [".zip", ".rar", ".7z", ".gz", ".bz2", ".xz", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz", ".zst", ".br", ".jar", ".war", ".ear", ".epub", ".jpg", ".jpeg", ".webp", ".avif", ".heic", ".heif", ".jp2", ".j2k", ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".flv", ".mpg", ".mpeg", ".wmv", ".ogv", ".3gp", ".3g2", ".mp3", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".flac", ".wma", ".wav"];

        public static bool IsCompresable(string? ext, long filesize)
        {
            if (filesize > MaxCompressionFileSize)
                return false;

            return !NonCompresableExt.Any(e => string.Equals(e, ext, StringComparison.InvariantCultureIgnoreCase));
        }

        public static CompressionLevel ShouldCompress(string? ext, long filesize)
        {
            if (IsCompresable(ext, filesize))
            {
                Console.Write("Do you want to compress this file? (you should not compress images, videos, zips or any similar packed or already compressed content) [Y,N]:");

                char response = GetConsoleKeyChar(['y', 's', 'n', 'o']);
                bool compress = response is 'y' or 's';

                Console.WriteLine('\n');

                if (!compress) return CompressionLevel.NoCompression;

                Console.Write("Select one of following options (fastest L1, optimal L4, smallest size L11) [F,O,S]:");
                char compressionLevel = GetConsoleKeyChar(['f', 'o', 's']);
                Console.WriteLine('\n');

                switch (compressionLevel)
                {
                    case 'f':
                        return CompressionLevel.Fastest;

                    case 'o':
                        return CompressionLevel.Optimal;

                    case 's':
                        return CompressionLevel.SmallestSize;

                    default:
                        return CompressionLevel.NoCompression;
                }
            }

            return CompressionLevel.NoCompression;
        }

        private static char GetConsoleKeyChar(char[] options)
        {
            ConsoleColor oldColor = Console.ForegroundColor;
            Console.ForegroundColor = Console.BackgroundColor;
            char response = char.MinValue;

            while ((response = char.ToLower(Console.ReadKey().KeyChar)) != null && !options.Any(c => char.ToLower(c) == response))
            {
                Console.Write('\b');
            }

            Console.ForegroundColor = oldColor;

            return response;
        }

        /// <summary>
        /// Encode part
        /// </summary>
        /// <param name="webHookId"></param>
        /// <param name="token"></param>
        /// <param name="buffer"></param>
        /// <param name="compress"></param>
        /// <returns></returns>
        ///
        private static Stopwatch sw = new Stopwatch();

        private static int MaxCompressionFileSize = (int.MaxValue / 8) * 7;

        public static async Task<Upload> Encode(WebHookHelper webHook, string name, Stream stream, CompressionLevel? level = CompressionLevel.NoCompression) => await EncodeCore(webHook, name, stream, level);

        public static async Task<Upload> EncodeCore(WebHookHelper webHook, string name, Stream stream, CompressionLevel? level = CompressionLevel.NoCompression, bool onTheFlyCompression = false, byte[] encodeKey = null, StreamWriter tempIdsWriter = null, bool disposeIdsWritter = true)
        {
            bool compress = level != CompressionLevel.NoCompression;

            tempIdsWriter = tempIdsWriter ?? UnsendedIdsWriter;

            using (MemoryStream seedData = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(seedData))
            {
                if (compress && !onTheFlyCompression)
                {
                    if (stream.Length >= MaxCompressionFileSize)
                        ErrorProgress.Report("Stream length is too big and can't be compressed to memory");

                    ConsoleProgress.Report(AnsiColors.Orange + "Compressing file please wait");

                    long originalFileSize = stream.Length;

                    var tempNewStream = new MemoryStream();

                    using (Stream origStream = stream)
                    using (var compStream = new BrotliStream(tempNewStream, (CompressionLevel)level, true))
                    {
                        int bytesRead;
                        long totalRead = 0;

                        byte[] buffer = new byte[Math.Max(origStream.Length / 100, 1)];

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                        {
                            await compStream.WriteAsync(buffer, 0, bytesRead);

                            totalRead += bytesRead;

                            double percentage = (double)totalRead / originalFileSize * 100;

                            ConsoleProgress.Report(AnsiColors.DarkYellow + $"Compressing please wait" + AnsiColors.DarkGray + ":" + AnsiColors.Silver + $" {percentage.ToString("0.0")}%");
                        }

                        await compStream.FlushAsync();
                    }

                    long compressedSize = tempNewStream.Length;
                    ConsoleProgress.Report(AnsiColors.Gold + "\nFile compressed " + AnsiColors.BrightCyan + Math.Round((compressedSize / (double)originalFileSize) * 100, 3) + AnsiColors.LightGray + "%" + AnsiColors.Gold + " compress ratio new size " + AnsiColors.Cyan + ByteSizeToString(compressedSize) + AnsiColors.Gold + " from " + AnsiColors.Cyan + ByteSizeToString(originalFileSize));
                    ConsoleProgress.Report("");
                    stream = tempNewStream;
                    stream.Position = 0;
                }

                //await seedData.WriteAsync(BitConverter.GetBytes(encodedSize = (ulong)dataStream.Length), 0, sizeof(ulong));

                int messagesToSend = (int)((ulong)stream.Length / CHUNK_SIZE) + 1, messagesSended = 0;

                List<ulong> attachmentsIdsList = new();
                List<ulong> messagesIdsList = [];

                long totalWrited = 0;

                ConsoleProgress.Report(AnsiColors.BrightGreen + "Starting upload of " + AnsiColors.Cyan + (compress ? "maximum " : null) + messagesToSend + AnsiColors.BrightGreen + " chunks " + AnsiColors.DarkGray + "(" + AnsiColors.Cyan + ByteSizeToString(stream.Length) + AnsiColors.DarkGray + ')');
                ConsoleProgress.Report("");

                sw.Start();

                byte[] key = encodeKey ?? RandomNumberGenerator.GetBytes(new Random().Next(10, 16));

                using (AesCTRStream ts = new AesCTRStream(null, key))
                {
                    var postChunk = async (byte[] buffer, int count, int index) =>
                    {
                        string attachementName = EncodeAttachementName(webHook.channelId, index, messagesToSend);

                        JsonNode? response = null;

                        while (response == null)
                        {
                            try
                            {
                                response = JsonNode.Parse(await webHook.PostFileToWebhook(attachementName, buffer, 0, count));

                                ulong attachementId = ulong.Parse((string)response["attachments"][0]["id"]);
                                ulong messageId = ulong.Parse((string)response["id"]);

                                if (attachementId <= 0 || messageId <= 0) throw new InvalidDataException("Failed to upload the chunk and retrieve the attachment");

                                tempIdsWriter.WriteLine(messageId.ToString());
                                tempIdsWriter.Flush();

                                attachmentsIdsList.Add(attachementId);
                                messagesIdsList.Add(messageId);
                            }
                            catch (Exception ex)
                            {
                                WriteException(ref ex, (response ?? "Uknown response").ToString());

                                Thread.Sleep(new Random().Next(0, 1000));
                            }
                        }

                        messagesSended++;
                        totalWrited += count;

                        timeList.Add(sw.ElapsedMilliseconds);
                        if (timeList.Count > MaxTimeListBuffer) timeList.RemoveAt(0);
                        long average = (timeList.Sum() / timeList.Count);
                        long totalTime = (messagesToSend - index - 1) * average;

                        ConsoleProgress.Report(AnsiColors.Gold + "Uploaded " + AnsiColors.Cyan + messagesSended + (!compress ? AnsiColors.Silver + "/" + AnsiColors.BrightCyan + messagesToSend : null) + AnsiColors.Gold + " total writed is " + AnsiColors.Silver + ByteSizeToString(totalWrited) + AnsiColors.Gold + " took " + AnsiColors.BrightBlue + sw.ElapsedMilliseconds + AnsiColors.Gold + "ms eta " + AnsiColors.LightBlue + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + AnsiColors.Gold + " end " + AnsiColors.LightGray + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss"));

                        if (sw.ElapsedMilliseconds < 2000)
                            Thread.Sleep(2000 - (int)sw.ElapsedMilliseconds);
                    };

                    if (compress && onTheFlyCompression)
                    {
                        int f = 0;
                        var pipe = new Pipe();
                        var writer = pipe.Writer;
                        var reader = pipe.Reader;

                        // Producer: compress into the pipe (runs concurrently)
                        var producer = Task.Run(async () =>
                        {
                            try
                            {
                                using (Stream pipeStream = writer.AsStream(leaveOpen: true))
                                using (var brotli = new BrotliStream(pipeStream, CompressionLevel.Optimal, leaveOpen: true))
                                {
                                    await stream.CopyToAsync(brotli, 81920);
                                    await brotli.FlushAsync();
                                }

                                await writer.CompleteAsync();
                            }
                            catch (Exception ex)
                            {
                                // Propagate to reader side
                                writer.Complete(ex);
                            }
                        });

                        // Consumer: read compressed bytes from the pipe and produce fixed-size chunks
                        var buffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
                        try
                        {
                            int filled = 0;
                            while (true)
                            {
                                ReadResult readResult = await reader.ReadAsync();
                                ReadOnlySequence<byte> seq = readResult.Buffer;
                                long consumedBytesTotal = 0;
                                bool emittedFullChunk = false;

                                // Consume the ReadOnlySequence in a loop, copying into rented buffer.
                                while (seq.Length > 0)
                                {
                                    int toCopy = (int)Math.Min(seq.Length, CHUNK_SIZE - filled);

                                    // Copy first toCopy bytes from seq into our buffer at 'filled'.
                                    // This creates a temporary span expression only — not stored across awaits.
                                    seq.Slice(0, toCopy).CopyTo(buffer.AsSpan(filled));

                                    filled += toCopy;
                                    consumedBytesTotal += toCopy;

                                    // Move our local view of seq forward.
                                    seq = seq.Slice(toCopy);

                                    // If we filled the chunk, advance the PipeReader (so the pipe drops the bytes),
                                    // then call the async callback (safe because no Span is alive).
                                    if (filled == CHUNK_SIZE)
                                    {
                                        var consumedPosition = readResult.Buffer.GetPosition(consumedBytesTotal);
                                        reader.AdvanceTo(consumedPosition, readResult.Buffer.End);

                                        ts.Encode(buffer, filled);
                                        await postChunk(buffer, filled, f++);
                                        sw.Restart();

                                        filled = 0;
                                        emittedFullChunk = true;
                                        break; // break inner loop and do another ReadAsync
                                    }
                                }

                                if (!emittedFullChunk)
                                {
                                    // We didn't emit a full chunk; inform the pipe how many bytes we consumed.
                                    var consumedPosition = readResult.Buffer.GetPosition(consumedBytesTotal);
                                    reader.AdvanceTo(consumedPosition, readResult.Buffer.End);
                                }

                                if (readResult.IsCompleted)
                                    break;

                                // continue to next ReadAsync
                            }

                            // Emit final partial chunk if any bytes remain
                            if (filled > 0)
                            {
                                ts.Encode(buffer, filled);
                                await postChunk(buffer, filled, f++);
                                sw.Restart();
                                filled = 0;
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);

                            await reader.CompleteAsync();
                        }

                        // Ensure producer completed and propagate any exceptions it threw
                        await producer;
                    }
                    else
                    {
                        byte[] buffer = new byte[CHUNK_SIZE];

                        for (int f = 0; f < messagesToSend; f++)
                        {
                            int bytesReadedTotal = 0, bytesReaded = 0;

                            while ((bytesReaded = await stream.ReadAsync(buffer, bytesReadedTotal, buffer.Length - bytesReadedTotal)) > 0)
                            {
                                bytesReadedTotal += bytesReaded;
                            }

                            ts.Encode(buffer, bytesReadedTotal);
                            await postChunk(buffer, bytesReadedTotal, f);
                            sw.Restart();
                        }
                    }
                }

                sw.Stop();

                if (disposeIdsWritter)
                    tempIdsWriter.BaseStream.SetLength(0);

                ConsoleProgress.Report("");

                ByteConfig config = new ByteConfig() { Compression = compress, VersionNumber = 2 };

                bw.Write(config.ToByte());
                bw.Write(totalWrited);
                bw.Write(webHook.channelId);
                bw.Write(new GorillaTimestampCompressor().Compress(attachmentsIdsList.ToArray()));

                try
                {
                    File.WriteAllBytes("seeds\\" + Directory.EnumerateFiles("seeds\\").Count(), seedData.ToArray());
                }
                catch { }

                return new Upload(name, seedData.ToArray().Deflate(), key, new GorillaTimestampCompressor().Compress(messagesIdsList.ToArray()), ref webHook);
            }
        }

        /// <summary>
        /// Decode part
        /// </summary>
        /// <param name="seed"></param>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public const int RefreshUrlsChunkSize = 50;

        public static async Task<ByteConfig> Decode(byte[] seed, byte[]? key, Stream stream) => await DecodeCore(seed, key, stream);

        public static async Task<ByteConfig> Decode(string seed, byte[]? key, Stream stream) => await DecodeCore(seed.FromBase64Url(), key, stream);

        private static async Task<ByteConfig> DecodeCore(byte[] seed, byte[]? key, Stream stream)
        {
            using (MemoryStream seedData = new MemoryStream(seed.Inflate()))
            using (BinaryReader br = new BinaryReader(seedData))
            {
                ByteConfig config = new ByteConfig(br.ReadByte());

                long totalSize = br.ReadInt64();
                ulong channelId = br.ReadUInt64();

                ulong[] attachmentsId = new GorillaTimestampCompressor().Decompress(br.ReadBytes((int)seedData.Length - (sizeof(ulong) + sizeof(long)) - sizeof(bool)));

                //Console.WriteLine(string.Join("\n", attachmentsId));

                int attachments = attachmentsId.Length;

                //Stream? outputStream = config.Compression ? StreamCompression.GetCompressorStream((ulong)aproxSize * 2) : stream;

                ConsoleProgress.Report("Downloading " + ByteSizeToString(totalSize) + "\n\n");

                string[] attachmentsUrls = attachmentsId.Select((id, index) => $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{EncodeAttachementName(channelId, index, (int)(totalSize / CHUNK_SIZE) + 1)}").ToArray();

                sw.Start();

                const int OutputBufferSize = (10 * 1024 * 1024) * 2;

                using (MemoryStream decoderCache = new MemoryStream())
                using (BrotliDecoder decoder = new BrotliDecoder())
                using (AesCTRStream ts = new AesCTRStream(null, key))
                {
                    int part = 0;
                    long downloaded = 0;

                    while (part < attachmentsUrls.Length)
                    {
                        string[] refreshedUrls = await RefreshUrls(attachmentsUrls.Skip(part).Take(attachmentsUrls.Length - part > 0 ? RefreshUrlsChunkSize : part - attachmentsUrls.Length).ToArray());

                        for (int e = part; e < part + RefreshUrlsChunkSize && e < attachmentsUrls.Length; e++)
                        {
                            sw.Restart();

                            string url = refreshedUrls[e - part];
                            long originalPosition = ts.Position;
                            byte[] chunk = null;

                            while (true)
                            {
                                try
                                {
                                    ConsoleProgress.Report($"Downloading id {attachmentsId[e]} {e + 1}/{attachments}");

                                    chunk = await client.GetByteArrayAsync(url);
                                    ts.Encode(chunk, chunk.Length);
                                    downloaded += chunk.Length;
                                }
                                catch (Exception ex)
                                {
                                    WriteException(ref ex);
                                    ts.Position = originalPosition;      // rewind the stream so the next write lands in the exact same spot
                                    Thread.Sleep(2500);
                                    continue;
                                }

                                if (config.Compression)
                                {
                                    await decoderCache.WriteAsync(chunk);

                                    while (decoderCache.Length > 0)
                                    {
                                        byte[] outBuf = ArrayPool<byte>.Shared.Rent(OutputBufferSize);

                                        try
                                        {
                                            var status = decoder.Decompress(
                                                decoderCache.ToArray(),
                                                outBuf,
                                                out int bytesConsumed,
                                                out int bytesWritten
                                            );

                                            decoderCache.RemoveFromStart(bytesConsumed);
                                            await stream.WriteAsync(outBuf, 0, bytesWritten);

                                            if (status == OperationStatus.InvalidData)
                                                throw new InvalidDataException("Invalid Brotli data.");
                                            else if (status == OperationStatus.NeedMoreData)
                                                break;
                                            else if (status == OperationStatus.DestinationTooSmall)
                                                throw new InvalidOperationException("Output buffer is too small for brotli output.");
                                        }
                                        finally
                                        {
                                            ArrayPool<byte>.Shared.Return(outBuf);
                                        }
                                    }
                                }
                                else
                                {
                                    await stream.WriteAsync(chunk);
                                }

                                break;
                            }

                            timeList.Add(sw.ElapsedMilliseconds);
                            if (timeList.Count > MaxTimeListBuffer) timeList.RemoveAt(0);
                            long average = (timeList.Sum() / timeList.Count);
                            long totalTime = (attachments - e) * average;

                            ConsoleProgress.Report(" downloaded " + ByteSizeToString(downloaded) + " took " + sw.ElapsedMilliseconds + "ms eta " + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + " end " + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss") + '\n');

                            //var decoded = dataPart;// U(dataPart, XorKey);
                            //await stream.WriteAsync(decoded, 0, dataPart.Length);
                        }

                        await stream.FlushAsync();

                        part += RefreshUrlsChunkSize;
                    }

                    ConsoleProgress.Report("\n\nFile downloaded\n");
                }

                sw.Stop();

                return config;
            }
        }

        public static Stream DecodeCorePipe(byte[] seed, byte[]? key)
        {
            var pipe = new Pipe();

            _ = Task.Run(async () =>
            {
                try
                {
                    await DecodeCore(seed, key, pipe.Writer.AsStream());
                    await pipe.Writer.CompleteAsync();
                }
                catch (Exception ex)
                {
                    pipe.Writer.Complete(ex);
                }
            });

            return pipe.Reader.AsStream();
        }

        private static void WriteException(ref Exception ex, params string[] messages)
        {
            var lastColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;

            ConsoleProgress.Report(string.Join('\n', messages.Where(m => !string.IsNullOrEmpty(m))) + '\n' + ex.ToString() + '\n');
            Console.WriteLine();
            Console.ForegroundColor = lastColor;
        }

        public class Upload
        {
            public string FileName { get; set; }
            public byte[] Seed { get; set; }
            public string SeedString { get => this.Seed.ToBase64Url() + (this.Key != null ? '$' + this.Key.ToBase64Url() : null); }
            public string DownloadToken { get => $"{Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(this.FileName)).BrotliCompress().ToBase64Url()}:{Path.GetExtension(this.FileName).TrimStart('.')}:{SeedString}"; }
            public byte[] Secret { get; set; }
            public byte[] Key { get; set; }
            public WebHookHelper WebHook { get; set; }
            public string RemoveToken { get => $"{this.Secret.ToBase64Url()}:{BitConverter.GetBytes(this.WebHook.id).ToBase64Url()}:{this.WebHook.token}"; }

            public string WebLink { get => $"https://df.gato.ovh/d/{SeedString}/{HttpUtility.UrlEncode(Encoding.UTF8.GetBytes(this.FileName))}"; }
            public string UploadLog { get; set; }
            public string Json { get => $"{{\"name\":\"{FileName}\",\"seed\":\"{SeedString}\",\"removeToken\":\"{RemoveToken}\",\"webLink\":\"{WebLink}\"}}"; }

            public Upload(string fileName, byte[] seed, byte[] key, byte[] secret, ref WebHookHelper webHookHelper)
            {
                this.FileName = fileName;
                this.Secret = secret;
                this.Seed = seed;
                this.Key = key;
                this.WebHook = webHookHelper;

                StringBuilder sb = new StringBuilder();

                sb.AppendLine($"FileName: `{fileName}`");
                sb.AppendLine($"Seed: `{this.Seed}`");
                sb.AppendLine($"RemoveToken: `{this.RemoveToken}`");

                /*this.Shortened = SendJspaste(fileSeed);

                 if (!string.IsNullOrEmpty(Shortened))
                 {
                     sb.AppendLine($"Shortened: `{this.Shortened}`");
                 }*/

                sb.AppendLine($"`WebLink:` {this.WebLink}");

                this.UploadLog = sb.ToString();
                string keyString = key.ToBase64Url();

                webHookHelper.SendMessageInChunks(string.Join("\n", this.UploadLog.Split('\n').Where(l => !l.Contains(keyString)))).GetAwaiter().GetResult();
            }

            /*private static string SendJspaste(string data)
            {
                try
                {
                    return "jsp:/" + JSPasteClient.Publish($"#DSFILES {assemblyVersion}\n\n" + data, new DocumentSettings()
                    {
                        LifeTime = TimeSpan.MaxValue,
                        KeyLength = 4,
                        Password = "hola",
                        Secret = "acrostico"
                    }).Result.Key;
                }
                catch (Exception ex)
                {
                    WriteException(ref ex);
                }

                return null;
            }*/
        }

        public class GorillaTimestampCompressor
        {
            private ulong _prevValue;
            private long _prevDelta;
            private BitWriter _out;
            private BitReader _in;

            private const long PrecomputedDelta = 19323285528;

            public byte[] Compress(ulong[] xs)
            {
                if ((xs[0] >> 62) != 0)
                    throw new InvalidOperationException("First timestamp exceeds 62 bits");

                _out = new BitWriter();
                _out.WriteBits(xs[0], 62);

                _prevValue = xs[0];
                _prevDelta = PrecomputedDelta;

                for (int i = 1; i < xs.Length; i++)
                {
                    ulong x = xs[i];
                    long delta = (long)(x - _prevValue);
                    long dod = delta - _prevDelta;

                    WriteDod(dod);

                    _prevValue = x;
                    _prevDelta = delta;
                }

                _out.Flush();
                return _out.ToArray();
            }

            public ulong[] Decompress(byte[] compressed)
            {
                var results = new List<ulong>();
                _in = new BitReader(compressed);

                _prevValue = _in.ReadBits(62);
                results.Add(_prevValue);

                _prevDelta = PrecomputedDelta;

                while (_in.BitsLeft >= 30)
                {
                    long dod = ReadDod();

                    long delta = _prevDelta + dod;
                    ulong value = _prevValue + (ulong)delta;

                    results.Add(value);

                    _prevValue = value;
                    _prevDelta = delta;
                }

                return results.ToArray();
            }

            /*private static int GetBits(long v)
            {
                ulong uv = ((ulong)(v << 1)) ^ (ulong)(v >> 63);

                for(int i = 0; i < 64;i++)
                {
                    if (uv < (1UL << i))
                    {
                        return i;
                    }
                }

                return 0;
            }*/

            private void WriteDod(long v)
            {
                ulong uv = ((ulong)(v << 1)) ^ (ulong)(v >> 63);

                //Console.Write($"[WriteDod] dod={v} uv=0x{uv:X}");

                if (uv < (1UL << 28))
                {
                    //Console.Write(" Bits:" + (2 + 28) + " Size:" + GetBits(v));
                    _out.WriteBits(0b00, 2);
                    _out.WriteBits(uv, 28);
                }
                else if (uv < (1UL << 30))
                {
                    //Console.Write(" Bits:" + (2 + 30) + " Size:" + GetBits(v));
                    _out.WriteBits(0b10, 2);
                    _out.WriteBits(uv, 30);
                }
                else if (uv < (1UL << 34))
                {
                    //Console.Write(" Bits:" + (2 + 34) + " Size:" + GetBits(v));
                    _out.WriteBits(0b01, 2);
                    _out.WriteBits(uv, 34);
                }
                else
                {
                    if (uv > (1UL << 40))
                    {
                        throw new InvalidOperationException("Dod encoder exceded maximun of 40 bits :c");
                    }

                    //Console.Write(" Bits:" + (2 + 40) + " Size:" + GetBits(v));
                    _out.WriteBits(0b11, 2);
                    _out.WriteBits(uv, 40);
                }

                //Console.Write("\n");
            }

            private long ReadDod()
            {
                int first = _in.ReadBit();
                int second = _in.ReadBit();
                int payloadBits = 0;

                if (first == 0 && second == 0)
                {
                    payloadBits = 28;
                }
                else if (first == 1 && second == 0)
                {
                    payloadBits = 30;
                }
                else if (first == 0 && second == 1)
                {
                    payloadBits = 34;
                }
                else if (first == 1 && second == 1)
                {
                    payloadBits = 40;
                }

                ulong uv = _in.ReadBits(payloadBits);
                long v = (long)((uv >> 1) ^ (ulong)-(long)(uv & 1));
                return v;
            }

            public class BitWriter
            {
                private readonly List<byte> _bytes = new();
                private int _bitPos = 0;
                private byte _cur = 0;

                public void WriteBit(int b)
                {
                    if (b != 0) _cur |= (byte)(1 << (7 - _bitPos));
                    _bitPos++;
                    if (_bitPos == 8) FlushByte();
                }

                public void WriteBits(ulong v, int count)
                {
                    for (int i = count - 1; i >= 0; i--)
                        WriteBit((int)((v >> i) & 1));
                }

                private void FlushByte()
                {
                    _bytes.Add(_cur);
                    _cur = 0;
                    _bitPos = 0;
                }

                public void Flush()
                {
                    if (_bitPos > 0) FlushByte();
                }

                public byte[] ToArray()
                    => _bytes.ToArray();
            }

            public class BitReader
            {
                private readonly byte[] _bytes;
                private int _bytePos = 0;
                private int _bitPos = 0;

                public BitReader(byte[] bytes)
                {
                    _bytes = bytes;
                }

                public long BitsLeft
                {
                    get
                    {
                        long bitsRead = _bytePos * 8L + _bitPos;
                        long totalBits = (long)_bytes.Length * 8L;
                        return totalBits - bitsRead;
                    }
                }

                public bool HasMoreBits
                {
                    get
                    {
                        long bitsRead = _bytePos * 8L + _bitPos;
                        long totalBits = (long)_bytes.Length * 8L;
                        return bitsRead < totalBits;
                    }
                }

                public int ReadBit()
                {
                    if (_bytePos >= _bytes.Length)
                        throw new InvalidOperationException("No more data available to read bits.");

                    int bit = (_bytes[_bytePos] >> (7 - _bitPos)) & 1;
                    _bitPos++;
                    if (_bitPos == 8)
                    {
                        _bitPos = 0;
                        _bytePos++;
                    }
                    return bit;
                }

                public ulong ReadBits(int count)
                {
                    if (count < 0 || count > 64)
                        throw new ArgumentOutOfRangeException(nameof(count));

                    ulong v = 0;
                    for (int i = 0; i < count; i++)
                    {
                        v = (v << 1) | (ulong)ReadBit();
                    }
                    return v;
                }
            }
        }

        private const long Kilobyte = 1000;
        private const long Megabyte = Kilobyte * 1000;
        private const long Gigabyte = Megabyte * 1000;
        private const long Terabyte = Gigabyte * 1000;
        private const long Petabyte = Terabyte * 1000;
        private const long Exabyte = Petabyte * 1000;

        private const string DecimalMask = "0.###";

        public static string ByteSizeToString(long size)
        {
            if (size > Exabyte) return (size / ((double)Exabyte)).ToString(DecimalMask) + "EB";
            else if (size > Petabyte) return (size / ((double)Petabyte)).ToString(DecimalMask) + "PB";
            else if (size > Terabyte) return (size / ((double)Terabyte)).ToString(DecimalMask) + "TB";
            else if (size > Gigabyte) return (size / ((double)Gigabyte)).ToString(DecimalMask) + "GB";
            else if (size > Megabyte) return (size / ((double)Megabyte)).ToString(DecimalMask) + "MB";
            else if (size > Kilobyte) return (size / ((double)Kilobyte)).ToString(DecimalMask) + "KB";
            else return size + "B";
        }

        public sealed class ByteConfig
        {
            public bool Compression { get; set; }
            public static int ClassVersion { get; set; } = 2;
            public int VersionNumber { get; set; }

            public ByteConfig()
            { }

            public ByteConfig(byte header)
            {
                this.Compression = (header & 0b10000000) != 0;
                this.VersionNumber = header & 0b00001111;

                if (VersionNumber == 0 || VersionNumber == 15) throw new ArgumentException("Byte header has been made with an unsuported byte");
            }

            public byte ToByte()
            {
                byte result = 0;

                if (Compression) result |= 0b10000000;

                result |= (byte)(VersionNumber & 0b00001111);
                return result;
            }
        }
    }
}