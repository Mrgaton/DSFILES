using System;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebHooksFileInMessagesEncoder;

namespace DSFiles
{
    public static class DiscordFilesSpliter
    {
        public static SHA256 hashAlg = SHA256.Create();
        public static StreamWriter UnsendedIdsWriter { get => File.AppendText(Program.UnsendedIds); }

        private static byte[] XorKey = Convert.FromBase64String("cQhZp1pcNUTi9+xi0JyoAyZ8Nc6KxtMTToWnSGPy2i4ICVbE9Byh6pkm88BoCVPi5lirZ2DG+x1u10XgE2tOjKBTIth+wIgKHdoFo9upjiPU5LlGnvcyk6M6CnehPmJoZIvRT63ac4kXXqZ3Y6SNLmdEZzMlIya7bDK3FSSKgbPE9dUIU2rr2ZZeawAXfd02FSbrB6Q0jYma8tLoTDiAIKKkEbPqNnEUeszO3darMQHO3hhTQ649uvMT8zYoBel8mwEcAhg+Y6IOD3kAw9bWlFwfJjyB+3zseoTDq9SMT23i3owa6pKGwyI4jAeUpk6jWALXOtyZ/Hu9veIUQNOG");

        /*public static IEnumerable<byte[]> SplitByLength(byte[] bytes, int maxLength)
        {
            for (int index = 0; index < bytes.Length; index += maxLength)
            {
                yield return bytes.Skip(index).Take(Math.Min(maxLength, bytes.Length - index)).ToArray();
            }
        }*/

        private const long amountPerFile = (25 * 1024 * 1024) - 256;

        private const int MaxTimeListBuffer = 10;

        private static List<long> timeList = new List<long>();

        public static async Task<string[]> RefreshUrls(string[] urls)
        {
            if (urls.Length > 50) throw new Exception("urls length cant be bigger than 50");

            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, "https://gato.ovh/attachments/refresh-urls"))
            {
                var data = JsonSerializer.Serialize(new Dictionary<string, object>() { { "attachment_urls", urls } });

                message.Content = new StringContent(data);

                using (HttpResponseMessage response = await new HttpClient().SendAsync(message))
                {
                    var str = await response.Content.ReadAsStringAsync();

                    //Console.WriteLine(str);

                    return JsonNode.Parse(str)["refreshed_urls"].AsArray().Select(element => (string)element["refreshed"]).ToArray();
                }
            }
        }

        private static string[] blackListedExt = [".zip", ".7z", ".rar", ".mp4", ".avi", ".png", ".jpg", ".iso"];

        public static CompressionLevel ShouldCompress(string ext, long filesize)
        {
            bool longTime = false;
            bool notUsefoul = blackListedExt.Any(e => e == ext);

            if (filesize > 512 * 1000 * 1000) longTime = true;

            if (longTime && !notUsefoul)
            {
                Console.Write("Do you want to compress this file? (it might take a long time) [Y,N]:");
            }
            else if (notUsefoul && !longTime)
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

            if (!compress) return CompressionLevel.NoCompression;

            Console.Write("Select one of following options (fastest, optimal, smallest size) [F,O,S]:");
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

        /*private static async Task<MemoryStream> TempolarStream(dynamic memStream, bool compress)
        {
            if (!compress) return memStream;

            using (MemoryStream tempStream = new MemoryStream())
            {
                using (DeflateStream dstream = new DeflateStream(tempStream, CompressionLevel.Optimal))
                {
                    memStream.Position = 0;

                    await memStream.CopyToAsync(dstream);
                }

                return new MemoryStream(tempStream.ToArray());
            }
        }

        private static async Task<FileStream> TempolarStream(FileStream stream, string filePath, bool compress)
        {
            if (!compress) return stream;

            using (FileStream tempStream = File.Open(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using (DeflateStream dstream = new DeflateStream(tempStream, CompressionLevel.Optimal))
                {
                    await stream.CopyToAsync(dstream);
                }
            }

            return File.Open(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }*/

        /// <summary>
        /// Encode part
        /// </summary>
        /// <param name="webHookId"></param>
        /// <param name="token"></param>
        /// <param name="buffer"></param>
        /// <param name="compress"></param>
        /// <returns></returns>
        public static async Task<(byte[] seed, byte[] secret)> Encode(WebHookHelper webHook, string filePath, CompressionLevel compressionLevel = CompressionLevel.NoCompression)
        {
            using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return await EncodeCore(webHook, stream, compressionLevel);
            }
        }

        public static async Task<(byte[] seed, byte[] secret)> Encode(WebHookHelper webHook, Stream fstream, CompressionLevel compressionLevel = CompressionLevel.NoCompression)
        {
            using (var stream = fstream)
            {
                return await EncodeCore(webHook, stream, compressionLevel);
            }
        }

        private static Stopwatch sw = new Stopwatch();

        public static async Task<(byte[] seed, byte[] secret)> EncodeCore(WebHookHelper webHook, Stream dataStream, CompressionLevel compressionLevel = CompressionLevel.NoCompression)
        {
            bool compress = compressionLevel != CompressionLevel.NoCompression;

            Stream tempCompressorStream = StreamCompression.GetCompressorStream((ulong)dataStream.Length);

            if (compress)
            {
                Console.WriteLine("Compressing file please wait");

                long originalFileSize = dataStream.Length;

                long totalRead = 0L;
                byte[] buffer = new byte[originalFileSize / (100 * 8)]; // 80 KB buffer
                int bytesRead;

                int consoleTop = Console.CursorTop - 1;

                using (var compStream = new BrotliStream(tempCompressorStream, compressionLevel, true))
                {
                    while ((bytesRead = await dataStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        await compStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        double percentage = (double)totalRead / dataStream.Length * 100;
                        string line = $"Compressing file please wait: {percentage.ToString(DecimalMask)}%";

                        Console.SetCursorPosition(0, consoleTop);
                        Console.Write(line + new string(' ', Console.WindowWidth - line.Length));
                        Console.SetCursorPosition(0, consoleTop);
                    }

                    Console.WriteLine("");

                    await compStream.FlushAsync();
                }

                await dataStream.DisposeAsync();

                dataStream = tempCompressorStream;

                long compressedSize = dataStream.Length;

                Console.WriteLine("");
                Console.WriteLine("File compressed " + Math.Round(((compressedSize / (double)originalFileSize) * 100), 3) + "% compress ratio new size " + ByteSizeToString(compressedSize));
                Console.WriteLine();
            }

            using (MemoryStream seedData = new MemoryStream())
            {
                seedData.WriteByte(compress ? (byte)255 : (byte)0);
                seedData.Write(BitConverter.GetBytes(webHook.channelId), 0, sizeof(ulong));

                List<ulong> attachementsIdsList = new List<ulong>();
                List<ulong> messagesIdsList = new List<ulong>();

                using (var tempIdsWriter = UnsendedIdsWriter)
                {
                    ulong lastAttachementId = int.MaxValue;

                    long totalWrited = 0;

                    int messagesToSend = (int)((ulong)dataStream.Length / amountPerFile) + 1, messagesSended = 0;

                    Console.WriteLine("Starting upload of " + messagesToSend + " chunks");
                    Console.WriteLine();

                    for (int i = 1; i - 1 < messagesToSend; i++)
                    {
                        sw.Restart();

                        byte[] buffer = new byte[amountPerFile * i > dataStream.Length ? (dataStream.Length - (amountPerFile * (i - 1))) : amountPerFile];

                        await dataStream.ReadAsync(buffer, 0, buffer.Length);

                        messagesSended++;

                        totalWrited += buffer.LongLength;

                    encodeRetry:

                        try
                        {
                            string attachementName = EncodeAttachementName(webHook.channelId, lastAttachementId, i, messagesToSend);

                            JsonNode response = JsonNode.Parse(await webHook.PostFileToWebhook(DXOR(buffer, XorKey), attachementName));

                            ulong attachementId = lastAttachementId = ulong.Parse((string)response["attachments"][0]["id"]);
                            ulong messageId = ulong.Parse((string)response["id"]);

                            tempIdsWriter.WriteLine(messageId.ToString());
                            tempIdsWriter.Flush();

                            attachementsIdsList.Add(attachementId);
                            messagesIdsList.Add(messageId);
                        }
                        catch (Exception ex)
                        {
                            var lastColor = Console.ForegroundColor;
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine('\n' + ex.ToString() + '\n');
                            Console.ForegroundColor = lastColor;

                            goto encodeRetry;
                        }

                        timeList.Add(sw.ElapsedMilliseconds);
                        if (timeList.Count > MaxTimeListBuffer) timeList.RemoveAt(0);
                        long average = (timeList.Sum() / timeList.Count);
                        long totalTime = (messagesToSend - i) * average;

                        Console.WriteLine("Uploaded " + messagesSended + "/" + messagesToSend + " total writed is " + ByteSizeToString(totalWrited) + " took " + sw.ElapsedMilliseconds + "ms eta " + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + " end " + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss") + " hash " + Base64Url.ToBase64Url(hashAlg.ComputeHash(buffer)) + " " + buffer.Length);

                        if (messagesSended == messagesToSend) break;

                        // Console.WriteLine(sw.ElapsedMilliseconds);git config --global user.email "your new email"

                        // Console.WriteLine("Sleeping to " + (sw.ElapsedMilliseconds > 2000 ? 0 : 2000 - (int)sw.ElapsedMilliseconds));

                        Thread.Sleep(sw.ElapsedMilliseconds > 2000 ? 0 : 2000 - (int)sw.ElapsedMilliseconds);
                    }

                    sw.Stop();
                }

                Console.WriteLine();

                File.WriteAllBytes(Program.UnsendedIds, []);

                WriteBuffer(CompressArray(attachementsIdsList.ToArray()), seedData);

                return (seedData.ToArray(), CompressArray(messagesIdsList.ToArray()));
            }
        }

        /// <summary>
        /// Decode part
        /// </summary>
        /// <param name="seed"></param>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public const int RefreshUrlsChunkSize = 50;

        public static async Task Decode(string seed, string filePath) => await Decode(seed.FromBase64Url(), filePath);

        public static async Task Decode(byte[] seed, string filePath)
        {
            using (FileStream stream = File.Open(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await DecodeCore(seed, stream);
            }
        }

        public static async Task<byte[]> Decode(string seed) => await Decode(seed.FromBase64Url());

        public static async Task<byte[]> Decode(byte[] seed)
        {
            using (MemoryStream memStream = new MemoryStream())
            {
                await DecodeCore(seed, memStream);

                return memStream.ToArray();
            }
        }

        public static async Task Decode(byte[] seed, Stream stream) => await DecodeCore(seed, stream);

        public static async Task Decode(string seed, Stream stream) => await DecodeCore(seed.FromBase64Url(), stream);

        private static async Task DecodeCore(byte[] seed, Stream stream)
        {
            using (MemoryStream seedData = new MemoryStream(seed))
            {
                bool compressed = seedData.ReadByte() == 255;

                Stream? originalStream = compressed ? stream : null;

                if (compressed) stream = StreamCompression.GetCompressorStream((ulong)stream.Length * 2);

                ulong channelId = BitConverter.ToUInt64(seedData.ReadAmout(sizeof(ulong)));

                ulong[] attachementsId = DecompressArray(seedData.ReadAmout(seedData.Length - sizeof(ulong) - sizeof(bool)));

                int attachements = attachementsId.Length;

                Console.WriteLine("Downloading file aprox size " + ByteSizeToString(attachements * amountPerFile));
                //IDsArrayCompressor.Decompress(seed.Skip(sizeof(ulong)).Take(seed.Length - sizeof(ulong)).ToArray()).ToArray();

                string[] attachementsUrls = new string[attachementsId.Length];

                for (int i = 0; i < attachementsUrls.Length; i++)
                {
                    ulong id = attachementsId[i];

                    attachementsUrls[i] = $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{EncodeAttachementName(channelId, i > 0 ? attachementsId[i - 1] : int.MaxValue, i + 1, attachements)}";
                }

                int part = 0;

                long downloaded = 0;

                sw.Start();

                using (HttpClient tempClient = new HttpClient())
                {
                    while (part < attachementsUrls.Length)
                    {
                        string[] refreshedUrls = await RefreshUrls(attachementsUrls.Skip(part).Take(attachementsUrls.Length - part > 0 ? RefreshUrlsChunkSize : part - attachementsUrls.Length).ToArray());

                        for (int e = part; e < part + RefreshUrlsChunkSize && e < attachementsUrls.Length; e++)
                        {
                            sw.Restart();

                            string url = refreshedUrls[e - part];

                        decodeRetry:

                            byte[] dataPart = null;

                            try
                            {
                                Console.Write("Downloading id " + attachementsId[e] + " " + (e + 1) + "/" + attachements);

                                dataPart = await tempClient.GetByteArrayAsync(url);
                            }
                            catch (Exception ex)
                            {
                                var lastColor = Console.ForegroundColor;
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine('\n' + ex.ToString() + '\n');
                                Console.ForegroundColor = lastColor;
                                Thread.Sleep(2000);

                                goto decodeRetry;
                            }

                            timeList.Add(sw.ElapsedMilliseconds);
                            if (timeList.Count > MaxTimeListBuffer) timeList.RemoveAt(0);
                            long average = (timeList.Sum() / timeList.Count);
                            long totalTime = (attachements - e) * average;

                            downloaded += dataPart.Length;

                            Console.WriteLine(" downloaded " + ByteSizeToString(downloaded) + " took " + sw.ElapsedMilliseconds + "ms eta " + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + " end " + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss") + " hash " + Base64Url.ToBase64Url(hashAlg.ComputeHash(UXOR(dataPart, XorKey))) + " " + dataPart.Length);

                            await stream.WriteAsync(UXOR(dataPart, XorKey), 0, dataPart.Length);
                        }

                        if (!compressed) await stream.FlushAsync();

                        part += RefreshUrlsChunkSize;
                    }

                    if (compressed)
                    {
                        stream.Position = 0;

                        using (var brstream = new BrotliStream(stream, CompressionMode.Decompress))
                        {
                            await brstream.CopyToAsync(originalStream);
                        }
                    }

                    //using (DeflateStream dstream = new DeflateStream(stream, CompressionMode.Decompress))
                    //{
                    for (int i = 0; i < attachements; i++)
                    {
                    }
                    //}

                    Console.WriteLine();
                    Console.WriteLine("File downloaded");
                }

                sw.Stop();
            }
        }

        /* private static async Task<dynamic> DecompressStream(dynamic tempStream, dynamic destStream)
         {
             using (DeflateStream dstream = new DeflateStream(tempStream, CompressionMode.Decompress))
             {
                 await dstream.CopyToAsync(destStream);
             }

             await destStream.FlushAsync();

             return destStream;
         }*/

        //private static Regex alphanumericRegex = new Regex("[^a-zA-Z0-9 -]");

        private static string EncodeAttachementName(ulong channelId, ulong lastMessage, int index, int amount) => Base64Url.ToBase64Url(BitConverter.GetBytes(channelId - lastMessage)).TrimEnd('=') + '_' + (amount - index);

        public static void WriteBuffer(byte[] buffer, dynamic Str) => Str.Write(buffer, 0, buffer.Length);

        /*public static class IDsArrayCompressor
        {
            public class DiscordSnowflake
            {
                private const long DiscordEpoch = 1420070400000;

                public ulong Snowflake { get; set; }

                public ulong Timestamp { get; set; }
                public uint WorkerId { get; set; }
                public uint ProcessId { get; set; }
                public uint Increment { get; set; }

                public DiscordSnowflake(ulong snowflake)
                {
                    Snowflake = snowflake;

                    Timestamp = (snowflake >> 22) + DiscordEpoch;
                    WorkerId = (uint)((snowflake & 0x3E0000) >> 17);
                    ProcessId = (uint)((snowflake & 0x1F000) >> 12);
                    Increment = (uint)(snowflake & 0xFFF);
                }

                public DiscordSnowflake(ulong timestamp, uint workerId, uint processId, uint increment)
                {
                    Timestamp = timestamp;
                    WorkerId = workerId;
                    ProcessId = processId;
                    Increment = increment;

                    Snowflake = ((timestamp - DiscordEpoch) << 22) | ((ulong)workerId << 17) | ((ulong)processId << 12) | (ulong)increment;
                }
            }

            private static byte[] ULongToByteArray(ulong value)
            {
                var bytes = BitConverter.GetBytes(value);

                if (BitConverter.IsLittleEndian) Array.Reverse(bytes);

                return bytes.SkipWhile(b => b == 0).DefaultIfEmpty().ToArray();
            }

            private static ulong ByteArrayToULong(byte[] bytes)
            {
                if (BitConverter.IsLittleEndian) Array.Reverse(bytes);

                return BitConverter.ToUInt64(bytes.Concat(new byte[8 - bytes.Length]).ToArray(), 0);
            }

            public static byte[] Compress(List<ulong> array)
            {
                Console.WriteLine("[" + string.Join(",", array) + "]");

                using (MemoryStream ms = new MemoryStream())
                {
                    uint workerId = new DiscordSnowflake(array[0]).WorkerId;

                    foreach (var snowflake in array.Select(id => new DiscordSnowflake(id)))
                    {
                        Console.WriteLine(snowflake.Timestamp);
                        Console.WriteLine(snowflake.Increment);
                        Console.WriteLine(snowflake.WorkerId);
                        Console.WriteLine(snowflake.ProcessId);

                        Console.WriteLine();
                    }

                    if (array.Select(id => new DiscordSnowflake(id)).Any(s => s.WorkerId > 15 || s.ProcessId > 15 || s.Increment > 255)) throw new Exception("Worker ID or processId is bigger than 4 bits or increment is bigger than 8");

                    List<DiscordSnowflake> decodedArray = new List<DiscordSnowflake>();

                    foreach (var showflake in array) decodedArray.Add(new DiscordSnowflake(showflake));

                    for (int i = array.Count - 1; i >= 0; i--) if (i - 1 >= 0) decodedArray[i].Timestamp = decodedArray[i].Timestamp - decodedArray[i - 1].Timestamp;

                    ms.WriteByte((byte)workerId);

                    for (int i = 0; i < array.Count; i++)
                    {
                        var snowflake = decodedArray[i];

                        Console.WriteLine(snowflake.Timestamp);

                        byte[] timespanArray = ULongToByteArray(snowflake.Timestamp);

                        byte[] metadata = ULongToByteArray(EncodeMetadata(snowflake.ProcessId, snowflake.Increment));

                        byte padding = CombineBits((byte)timespanArray.Length, (byte)metadata.Length);

                        ms.WriteByte(padding);
                        ms.Write(timespanArray, 0, timespanArray.Length);
                        ms.Write(metadata, 0, metadata.Length);
                    }

                    return ms.ToArray();
                }
            }

            public static IEnumerable<ulong> Decompress(byte[] data)
            {
                using (MemoryStream ms = new MemoryStream(data))
                {
                    uint workerId = (uint)ms.ReadByte();

                    ulong lastValue = 0;

                    while (ms.Position < ms.Length)
                    {
                        SplitByte((byte)ms.ReadByte(), out byte timespanLengh, out byte metadataLengh);

                        byte[] timespanBuffer = new byte[timespanLengh];
                        ms.Read(timespanBuffer, 0, timespanBuffer.Length);
                        lastValue += ByteArrayToULong(timespanBuffer);

                        byte[] metadataBuffer = new byte[metadataLengh];
                        ms.Read(metadataBuffer, 0, metadataBuffer.Length);

                        DecodeMetadata((int)ByteArrayToULong(metadataBuffer), out ushort processId, out ushort increment);

                        yield return new DiscordSnowflake(lastValue, workerId, processId, increment).Snowflake;
                    }
                }
            }

            public static ulong EncodeMetadata(uint processId, uint increment) => (processId << 12) | increment;

            public static void DecodeMetadata(int snowflake, out ushort processId, out ushort increment)
            {
                processId = (ushort)((snowflake & 0x1F000) >> 12);
                increment = (ushort)(snowflake & 0xFFF);
            }

            public static byte CombineBits(byte first, byte second) => (byte)((first << 4) | second);

            public static void SplitByte(byte combined, out byte first, out byte second)
            {
                first = (byte)(combined >> 4);
                second = (byte)(combined & 0x0F);
            }
        }*/

        public static byte[] CompressArray(ulong[] array) => ArraySerealizer(array).Compress();

        public static ulong[] DecompressArray(byte[] data) => ArrayDeserealizer(data.Decompress());

        private static ulong GetDeltaMin(ulong[] nums)
        {
            ulong[] diff = new ulong[nums.Length - 1];

            for (int i = 0; i < diff.Length; i++)
            {
                diff[i] = nums[i + 1] - nums[i];
            }

            ulong min = ulong.MaxValue;

            for (int i = 0; i < diff.Length - 1; i++) min = Math.Min(diff[i], min);

            return min;
        }

        private static byte[] ArraySerealizer(ulong[] nums)
        {
            ulong deltaMin = GetDeltaMin(nums);

            ulong last = 0;

            using (MemoryStream memStr = new MemoryStream())
            {
                memStr.Write(BitConverter.GetBytes(deltaMin));

                for (int i = 0; i < nums.Length; i++)
                {
                    ulong n = nums[i];

                    var bytes = BitConverter.GetBytes(n - last - deltaMin);

                    if (i % 2 == 0) Array.Reverse(bytes);

                    memStr.Write(bytes);

                    last = n;
                }

                return memStr.ToArray();
            }
        }

        private static ulong[] ArrayDeserealizer(byte[] data)
        {
            using (MemoryStream memStr = new MemoryStream(data))
            {
                ulong deltaMin = memStr.ReadULong(false);

                ulong last = 0;

                ulong[] array = new ulong[(memStr.Length / sizeof(ulong)) - 1];

                for (int i = 0; i < array.Length; i++)
                {
                    ulong num = memStr.ReadULong(i % 2 == 0);

                    array[i] = num + last + deltaMin;

                    last = array[i];
                }

                return array;
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

        private static byte[] DXOR(byte[] data, byte[] key)
        {
            byte[] result = new byte[data.Length];
            int max = data.Length - 1;
            for (int i = 0; i < data.Length; i++) result[i] = (byte)(data[max - i] ^ (key[i % key.Length] + i));
            return result;
        }

        private static byte[] UXOR(byte[] data, byte[] key)
        {
            byte[] result = new byte[data.Length];
            int max = data.Length - 1;
            for (int i = data.Length - 1; i >= 0; i--) result[max - i] = (byte)(data[i] ^ (key[i % key.Length] + i));
            return result;
        }
    }
}