using JSPasteNet;
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Web;

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

        private static string[] blackListedExt = [".zip", ".7z", ".rar", ".mp4", ".avi", ".png", ".jpg", ".iso"];

        public static string EncodeAttachementName(ulong channelId, int index, int amount) => (BitConverter.GetBytes((channelId) ^ (ulong)index ^ (ulong)amount)).ToBase64Url().TrimStart('_') + '_' + (amount - index);
        private static int SelectQuality(long length)
        {
            if (length > 125L << 20) return 4;
            else if (length > 100L << 20) return 5;
            else if (length > 75L << 20) return 7;
            else if (length > 55L << 20) return 9;
            else if (length > 25L << 20) return 10;
            else return 11;
        }
        public static int ShouldCompress(string? ext, long filesize, bool askToNotCompress = true)
        {
            bool longTime = false;
            bool notUseful = blackListedExt.Any(e => e == ext);

            if (filesize > 512 * 1000 * 1000) longTime = true;

            if (!notUseful)
            {
                if (askToNotCompress)
                {
                    if (!longTime && !notUseful)
                    {
                        Console.Write("Do you want to compress this file? [Y,N]:");
                    }
                    else if (longTime && !notUseful)
                    {
                        Console.Write("Do you want to compress this file? (it might take a long time) [Y,N]:");
                    }
                    else if (notUseful && !longTime)
                    {
                        Console.Write("Do you want to compress this file? (it will probably not be useful) [Y,N]:");
                    }
                    else
                    {
                        Console.Write("Do you want to compress this file? (it wont be useful and will take a lot of time) [Y,N]:");
                    }

                    char response = GetConsoleKeyChar(['y', 's', 'n']);
                    bool compress = response is 'y' or 's';
                    Console.WriteLine('\n');

                    if (!compress) return 0;

                    Console.Write("Select one of following options (fastest, optimal, smallest size) [F,O,S]:");
                    char compressionLevel = GetConsoleKeyChar(['f', 'o', 's']);
                    Console.WriteLine('\n');

                    switch (compressionLevel)
                    {
                        case 'f':
                            return 1;

                        case 'o':
                            return 4;

                        case 's':
                            return 11;

                        default:
                            return 0;
                    }
                }
                else
                {
                    return SelectQuality(filesize);
                }
            }

            return 0;
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

        public static async Task<Upload> Encode(WebHookHelper webHook, string name, Stream stream, int compressionLevel = 4) => await EncodeCore(webHook, name, stream, compressionLevel);

        public static async Task<Upload> EncodeCore(WebHookHelper webHook, string name, Stream stream, int compressionLevel = 4)
        {
            bool compress = compressionLevel > 0;

            using (MemoryStream seedData = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(seedData))
            {
                ByteConfig config = new ByteConfig() { Compression = compress, VersionNumber = 2 };

                bw.Write(config.ToByte());
                bw.Write(stream.Length);
                bw.Write(webHook.channelId);

                //await seedData.WriteAsync(BitConverter.GetBytes(encodedSize = (ulong)dataStream.Length), 0, sizeof(ulong));

                int messagesToSend = (int)((ulong)stream.Length / CHUNK_SIZE) + 1, messagesSended = 0;

                List<ulong> attachmentsIdsList = new();
                List<ulong> messagesIdsList = [];

                using StreamWriter tempIdsWriter = UnsendedIdsWriter;

                long totalWrited = 0;

                Console.WriteLine("Starting upload of " + (compress ? "maximum " : null) + messagesToSend + " chunks (" + ByteSizeToString(stream.Length) + ')');
                Console.WriteLine();

                sw.Start();

                byte[] key = RandomNumberGenerator.GetBytes(32);

                using (AesCTRStream ts = new AesCTRStream(null, key))
                {
                    var postChunk = (byte[] buffer, int count, int index) =>
                    {
                        string attachementName = EncodeAttachementName(webHook.channelId, index, messagesToSend);

                        JsonNode? response = null;

                        while (response == null)
                        {
                            try
                            {
                                response = JsonNode.Parse(webHook.PostFileToWebhook(attachementName, buffer, 0, count).Result);

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

                        Console.WriteLine("Uploaded " + messagesSended + (!compress ? "/" + messagesToSend : null) + " total writed is " + ByteSizeToString(totalWrited) + " took " + sw.ElapsedMilliseconds + "ms eta " + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + " end " + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss"));

                        if (sw.ElapsedMilliseconds < 2000) Thread.Sleep(2000 - (int)sw.ElapsedMilliseconds);
                    };

                    int i = 0;

                    if (compress)
                    {
                        var pendingBuffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE + IOBuffer);
                        int pendingCount = 0;

                        byte[] inputBuffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);
                        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(CHUNK_SIZE);

                        try
                        {
                            using (var encoder = new BrotliEncoder(compressionLevel, window: 24))
                            {
                                bool eof = false;
                                while (!eof)
                                {
                                    int read = await stream.ReadAsync(inputBuffer, 0, IOBuffer);
                                    if (read == 0) eof = true;

                                    var inputSpan = new ReadOnlySpan<byte>(inputBuffer, 0, read);
                                    while (true)
                                    {
                                        bool isFinal = eof && inputSpan.IsEmpty;
                                        var status = encoder.Compress(inputSpan, outputBuffer, out int consumed, out int produced, isFinal);

                                        new ReadOnlySpan<byte>(outputBuffer, 0, produced)
                                            .CopyTo(new Span<byte>(pendingBuffer, pendingCount, produced));

                                        pendingCount += produced;

                                        if (pendingCount >= CHUNK_SIZE)
                                        {
                                            ts.Encode(pendingBuffer, CHUNK_SIZE);
                                            postChunk(pendingBuffer, CHUNK_SIZE, i++);
                                            sw.Restart();

                                            int tail = pendingCount - CHUNK_SIZE;
                                            if (tail > 0)
                                                Buffer.BlockCopy(pendingBuffer, CHUNK_SIZE, pendingBuffer, 0, tail);
                                            pendingCount = tail;
                                        }

                                        if (status == OperationStatus.Done)
                                            break;

                                        if (status == OperationStatus.NeedMoreData)
                                        {
                                            inputSpan = inputSpan.Slice(consumed);
                                            break;
                                        }
                                        if (status == OperationStatus.DestinationTooSmall)
                                        {
                                            inputSpan = inputSpan.Slice(consumed);
                                            continue;
                                        }
                                        throw new InvalidOperationException($"Brotli error: {status}");
                                    }
                                }

                                while (true)
                                {
                                    var status = encoder.Flush(outputBuffer, out int produced);
                                    new ReadOnlySpan<byte>(outputBuffer, 0, produced)
                                        .CopyTo(new Span<byte>(pendingBuffer, pendingCount, produced));
                                    pendingCount += produced;

                                    if (pendingCount >= CHUNK_SIZE)
                                    {
                                        ts.Encode(pendingBuffer, CHUNK_SIZE);
                                        postChunk(pendingBuffer, CHUNK_SIZE, i++);
                                        sw.Restart();

                                        int tail = pendingCount - CHUNK_SIZE;
                                        if (tail > 0)
                                            Buffer.BlockCopy(pendingBuffer, CHUNK_SIZE, pendingBuffer, 0, tail);
                                        pendingCount = tail;
                                    }

                                    if (status == OperationStatus.Done) break;
                                    if (status != OperationStatus.DestinationTooSmall)
                                        throw new InvalidOperationException($"Brotli flush failed: {status}");
                                }

                                // write any remaining bytes (< chunkSize)
                                if (pendingCount > 0)
                                {
                                    ts.Encode(pendingBuffer, pendingCount);

                                    postChunk(pendingBuffer, pendingCount, i++); // how can we know the exact amount remaning?
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(pendingBuffer);
                            ArrayPool<byte>.Shared.Return(inputBuffer);
                            ArrayPool<byte>.Shared.Return(outputBuffer);
                        }
                    }
                    else
                    {
                        byte[] buffer = new byte[CHUNK_SIZE];

                        for (int f = 0; f < messagesToSend; f++)
                        {
                            int bytesReaded = await stream.ReadAsync(buffer, 0, buffer.Length);
                            ts.Encode(buffer, bytesReaded);
                            postChunk(buffer, bytesReaded, f);
                            sw.Restart();
                        }
                    }
                }

                sw.Stop();

                tempIdsWriter.BaseStream.SetLength(0);

                Console.WriteLine();

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

        public static async Task<ByteConfig> Decode(byte[] seed, byte[] key, Stream stream) => await DecodeCore(seed, key, stream);

        public static async Task<ByteConfig> Decode(string seed, byte[] key, Stream stream) => await DecodeCore(seed.FromBase64Url(), key, stream);

        private static async Task<ByteConfig> DecodeCore(byte[] seed, byte[] key, Stream stream)
        {
            using (MemoryStream seedData = new MemoryStream(seed.Inflate()))
            using (BinaryReader br = new BinaryReader(seedData))
            {
                ByteConfig config = new ByteConfig(br.ReadByte());

                long totalSize = br.ReadInt64();
                ulong channelId = br.ReadUInt64();

                ulong[] attachmentsId = new GorillaTimestampCompressor().Decompress(br.ReadBytes((int)seedData.Length - (sizeof(ulong) + sizeof(long)) - sizeof(bool)));

                int attachments = attachmentsId.Length;
                long aproxSize = attachments * CHUNK_SIZE;

                //Stream? outputStream = config.Compression ? StreamCompression.GetCompressorStream((ulong)aproxSize * 2) : stream;

                Console.WriteLine("Downloading " + ByteSizeToString(totalSize) + '\n');

                string[] attachmentsUrls = attachmentsId.Select((id, index) => $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{EncodeAttachementName(channelId, index, (int)(totalSize / CHUNK_SIZE) + 1)}").ToArray();

                sw.Start();

                
                const int OutputBufferSize = 64 * 1024;

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
                                    Console.Write($"Downloading id {attachmentsId[e]} {e + 1}/{attachments}");

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
                                    var outputBuffer = new ArrayBufferWriter<byte>(chunk.Length * 2);
                                    ReadOnlySpan<byte> span = chunk;

                                    while (!span.IsEmpty)
                                    {
                                        byte[] outBuf = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
                                        try
                                        {
                                            var status = decoder.Decompress(
                                                span,
                                                outBuf,
                                                out int bytesConsumed,
                                                out int bytesWritten
                                            );
                                            if (status == OperationStatus.InvalidData)
                                                throw new InvalidDataException("Invalid Brotli data.");

                                            // Copy the decompressed bytes into our buffer
                                            outputBuffer.Write(outBuf.AsSpan(0, bytesWritten));

                                            // Advance the span
                                            span = span.Slice(bytesConsumed);

                                            if (status == OperationStatus.NeedMoreData)
                                                break;
                                        }
                                        finally
                                        {
                                            ArrayPool<byte>.Shared.Return(outBuf);
                                        }
                                    }

                                    await stream.WriteAsync(outputBuffer.WrittenMemory);
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

                            Console.WriteLine(" downloaded " + ByteSizeToString(downloaded) + " took " + sw.ElapsedMilliseconds + "ms eta " + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + " end " + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss"));

                            //var decoded = dataPart;// U(dataPart, XorKey);
                            //await stream.WriteAsync(decoded, 0, dataPart.Length);
                        }

                        await stream.FlushAsync();

                        part += RefreshUrlsChunkSize;
                    }

                    Console.WriteLine();

                    Console.WriteLine("File downloaded");
                }

                sw.Stop();

                return config;
            }
        }
        public static Stream DecodeCorePipe(byte[] seed, byte[] key)
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
            Console.WriteLine(string.Join('\n', messages.Where(m => !string.IsNullOrEmpty(m))) + '\n' + ex.ToString() + '\n');
            Console.ForegroundColor = lastColor;
        }

        private static string assemblyVersion = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        public class Upload
        {
            public string FileName { get; set; }
            public string Seed { get; set; }
            public string RemoveToken { get; set; }
            public string WebLink { get; set; }
            public string? Shortened { get; set; }
            public string UploadLog { get; set; }

            public string ToJson()
            {
                return '{' + $"\"name\":\"{FileName}\",\"seed\":\"{Seed}\",\"removeToken\":\"{RemoveToken}\",\"webLink\":\"{WebLink}\",\"shortened\":\"{Shortened}\"" + '}';
            }

            public Upload(string fileName, byte[] seed, byte[] key, byte[] secret, ref WebHookHelper webHookHelper)
            {
                string extension = Path.GetExtension(fileName).TrimStart('.');
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string keyString = key.ToBase64Url();

                string fileSeed = Encoding.UTF8.GetBytes(fileNameWithoutExtension).BrotliCompress().ToBase64Url()
                    + ':' + extension
                    + ':' + seed.ToBase64Url() + (key != null ? '$' + keyString : null)
                    + '/' + secret.ToBase64Url()
                    + ':' + BitConverter.GetBytes(webHookHelper.id).ToBase64Url()
                    + ':' + webHookHelper.token;

                string[] seedSplited = fileSeed.Split('/');

                this.FileName = fileName;
                this.Seed = seedSplited[0];
                this.RemoveToken = seedSplited.Last();
                this.WebLink = $"https://df.gato.ovh/d/{seedSplited[0].Split(':').Last()}/{HttpUtility.UrlEncode(Encoding.UTF8.GetBytes(fileName))}";

                StringBuilder sb = new StringBuilder();

                sb.AppendLine($"FileName: `{fileName}`");
                sb.AppendLine($"Seed: `{this.Seed}`");
                sb.AppendLine($"RemoveToken: `{this.RemoveToken}`");

                this.Shortened = SendJspaste(fileSeed);

                sb.AppendLine($"Shortened: `{this.Shortened}`");
                sb.AppendLine($"`WebLink:` {this.WebLink}");

                this.UploadLog = sb.ToString();


                webHookHelper.SendMessageInChunks(string.Join("\n", this.UploadLog.Split('\n').Where(l => !l.Contains(keyString)))).GetAwaiter().GetResult();
            }

            private static string SendJspaste(string data)
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

                return data;
            }
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

                // 1) Read first timestamp in full
                _prevValue = _in.ReadBits(62);
                results.Add(_prevValue);

                _prevDelta = PrecomputedDelta;  // Δ₀ = 0

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
                // ZigZag‐encode to unsigned
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
                        throw new InvalidOperationException("Dod exceded maximun of 40 bits :c");
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