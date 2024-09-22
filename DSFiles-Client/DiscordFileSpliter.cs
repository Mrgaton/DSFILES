using DSFiles_Client;
using DSFiles_Client.Utils;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSFiles
{
    public static class DiscordFilesSpliter
    {
        public static StreamWriter UnsendedIdsWriter { get => new StreamWriter(File.Open(Program.UnsendedIds, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { AutoFlush = true }; }

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

        public static string EncodeAttachementName(ulong channelId, int index, int amount) => Base64Url.ToBase64Url(BitConverter.GetBytes((channelId) ^ (ulong)index ^ (ulong)amount)).TrimStart('_') + '_' + (amount - index);

        public static CompressionLevel ShouldCompress(string? ext, long filesize, bool askToNotCompress = true)
        {
            bool longTime = false;
            bool notUseful = blackListedExt.Any(e => e == ext);

            if (filesize > 512 * 1000 * 1000) longTime = true;

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

                if (!compress) return CompressionLevel.NoCompression;
            }

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

        /// <summary>
        /// Encode part
        /// </summary>
        /// <param name="webHookId"></param>
        /// <param name="token"></param>
        /// <param name="buffer"></param>
        /// <param name="compress"></param>
        /// <returns></returns>

        public static async Task<(byte[] seed, byte[] secret, ulong size)> Encode(WebHookHelper webHook, Stream stream, CompressionLevel compressionLevel = CompressionLevel.NoCompression)
        {
            return await EncodeCore(webHook, stream, compressionLevel);
        }

        private static Stopwatch sw = new Stopwatch();

        public static async Task<(byte[] seed, byte[] secret, ulong size)> EncodeCore(WebHookHelper webHook, Stream stream, CompressionLevel compressionLevel = CompressionLevel.NoCompression)
        {
            ulong encodedSize = 0;

            bool compress = compressionLevel != CompressionLevel.NoCompression;

            Stream uploadStream = compress ? StreamCompression.GetCompressorStream((ulong)stream.Length) : stream;

            if (compress)
            {
                Console.WriteLine("Compressing file please wait");

                long originalFileSize = stream.Length;

                using (var compStream = new BrotliStream(uploadStream, compressionLevel, true))
                {
                    int bytesRead;
                    long totalRead = 0;

                    byte[] buffer = new byte[Math.Max(originalFileSize / (100 * 8), 1)];

                    int consoleTop = Console.CursorTop - 1;

                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        await compStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        double percentage = (double)totalRead / stream.Length * 100;
                        string line = $"Compressing file please wait: {percentage.ToString(DecimalMask)}%";

                        Console.SetCursorPosition(0, consoleTop);
                        Console.Write(line + new string(' ', Console.WindowWidth - line.Length));
                        Console.SetCursorPosition(0, consoleTop);
                    }

                    await compStream.FlushAsync();
                }

                await stream.DisposeAsync();

                long compressedSize = uploadStream.Length;
                Console.WriteLine("File compressed " + Math.Round((compressedSize / (double)originalFileSize) * 100, 3) + "% compress ratio new size " + ByteSizeToString(compressedSize));
                Console.WriteLine();

                uploadStream.Position = 0;
            }

            using (MemoryStream seedData = new MemoryStream())
            {
                ByteConfig config = new ByteConfig() { Compression = compress };

                seedData.WriteByte(config.ToByte());
                seedData.Write(BitConverter.GetBytes((uint)(uploadStream.Length % amountPerFile)));
                seedData.Write(BitConverter.GetBytes(webHook.channelId));

                //await seedData.WriteAsync(BitConverter.GetBytes(encodedSize = (ulong)dataStream.Length), 0, sizeof(ulong));

                int messagesToSend = (int)((ulong)uploadStream.Length / amountPerFile) + 1, messagesSended = 0;

                ulong[] attachementsIdsList = new ulong[messagesToSend];

                List<ulong> messagesIdsList = [];

                using StreamWriter tempIdsWriter = UnsendedIdsWriter;

                long totalWrited = 0;

                Console.WriteLine("Starting upload of " + messagesToSend + " chunks (" + ByteSizeToString(uploadStream.Length) + ')');
                Console.WriteLine();

                using (TransformStream ts = new TransformStream(uploadStream))
                {
                    for (int i = 1; i - 1 < messagesToSend; i++)
                    {
                        sw.Restart();

                        byte[] buffer = new byte[amountPerFile * i > ts.Length ? (ts.Length - (amountPerFile * (i - 1))) : amountPerFile];

                        await ts.ReadAsync(buffer, 0, buffer.Length);

                        string attachementName = EncodeAttachementName(webHook.channelId, i, messagesToSend);


                    encodeRetry:

                        JsonNode? response = null;

                        try
                        {
                            response = JsonNode.Parse(await webHook.PostFileToWebhook(attachementName, buffer));

                            var attachementId = attachementsIdsList[i - 1] = ulong.Parse((string)response["attachments"][0]["id"]);

                            ulong messageId = ulong.Parse((string)response["id"]);

                            if (attachementId <= 0 || messageId <= 0) throw new InvalidDataException("Failed to upload the chunk and retrieve the attachment");

                            await tempIdsWriter.WriteLineAsync(messageId.ToString());
                            await tempIdsWriter.FlushAsync();

                            messagesIdsList.Add(messageId);
                        }
                        catch (Exception ex)
                        {
                            Program.WriteException(ref ex, response.ToString());
                            Thread.Sleep(new Random().Next(0, 1000));
                            goto encodeRetry;
                        }

                        messagesSended += 1;
                        totalWrited += buffer.LongLength;

                        timeList.Add(sw.ElapsedMilliseconds);
                        if (timeList.Count > MaxTimeListBuffer) timeList.RemoveAt(0);
                        long average = (timeList.Sum() / timeList.Count);
                        long totalTime = (messagesToSend - i) * average;

                        Console.WriteLine("Uploaded " + messagesSended + "/" + messagesToSend + " total writed is " + ByteSizeToString(totalWrited) + " took " + sw.ElapsedMilliseconds + "ms eta " + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + " end " + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss"));

                        if (messagesSended == messagesToSend) break;

                        if (sw.ElapsedMilliseconds < 2000) Thread.Sleep(2000 - (int)sw.ElapsedMilliseconds);
                    }
                }

                sw.Stop();

                tempIdsWriter.BaseStream.SetLength(0);

                Console.WriteLine();

                await seedData.WriteAsync(CompressArray(attachementsIdsList));

                try
                {
                    File.WriteAllBytes("seeds\\" + Directory.EnumerateFiles("seeds\\").Count(), seedData.ToArray());
                }
                catch { }

                return (seedData.ToArray().Deflate(), CompressArray(messagesIdsList.ToArray()).Deflate(), encodedSize);
            }
        }

        /// <summary>
        /// Decode part
        /// </summary>
        /// <param name="seed"></param>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public const int RefreshUrlsChunkSize = 50;

        public static async Task Decode(byte[] seed, Stream stream) => await DecodeCore(seed, stream);

        public static async Task Decode(string seed, Stream stream) => await DecodeCore(seed.FromBase64Url(), stream);

        private static async Task DecodeCore(byte[] seed, Stream stream)
        {
            using (MemoryStream seedData = new MemoryStream(seed.Inflate()))
            {
                ByteConfig config = new ByteConfig((byte)seedData.ReadByte());

                uint sizeInterval = BitConverter.ToUInt32(seedData.ReadAmout(sizeof(uint)));

                ulong channelId = BitConverter.ToUInt64(seedData.ReadAmout(sizeof(ulong)));

                //ulong contentLength = BitConverter.ToUInt64(seedData.ReadAmout(sizeof(ulong)));

                ulong[] attachementsId = DecompressArray(seedData.ReadAmout(seedData.Length - (sizeof(ulong) * 1) - sizeof(bool)));

                int attachements = attachementsId.Length;

                long aproxSize = attachements * amountPerFile;

                Stream? outputStream = config.Compression ? StreamCompression.GetCompressorStream((ulong)(aproxSize) * 2) : stream;

                Console.WriteLine("Downloading aprox max size " + ByteSizeToString(aproxSize) + '\n');

                string[] attachementsUrls = new string[attachementsId.Length];

                for (int i = 0; i < attachementsUrls.Length; i++)
                {
                    ulong id = attachementsId[i];

                    attachementsUrls[i] = $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{EncodeAttachementName(channelId, i + 1, attachements)}";
                }

                sw.Start();

                using (HttpClient tempClient = new HttpClient())
                {
                    int part = 0;

                    long downloaded = 0;

                    using (TransformStream ts = new TransformStream(outputStream))
                    {
                        while (part < attachementsUrls.Length)
                        {
                            string[] refreshedUrls = await RefreshUrls(attachementsUrls.Skip(part).Take(attachementsUrls.Length - part > 0 ? RefreshUrlsChunkSize : part - attachementsUrls.Length).ToArray());

                            for (int e = part; e < part + RefreshUrlsChunkSize && e < attachementsUrls.Length; e++)
                            {
                                sw.Restart();

                                string url = refreshedUrls[e - part];

                                byte[] chunk = null;

                            rety:
                                try
                                {
                                    Console.Write("Downloading id " + attachementsId[e] + " " + (e + 1) + "/" + attachements);

                                    chunk = await tempClient.GetByteArrayAsync(url);
                                }
                                catch (Exception ex)
                                {
                                    Program.WriteException(ref ex);

                                    goto rety;
                                }

                                await ts.WriteAsync(chunk);
                                downloaded += chunk.Length;

                                timeList.Add(sw.ElapsedMilliseconds);
                                if (timeList.Count > MaxTimeListBuffer) timeList.RemoveAt(0);
                                long average = (timeList.Sum() / timeList.Count);
                                long totalTime = (attachements - e) * average;

                                Console.WriteLine(" downloaded " + ByteSizeToString(downloaded) + " took " + sw.ElapsedMilliseconds + "ms eta " + TimeSpan.FromMilliseconds(totalTime).ToReadableString() + " end " + DateTime.Now.AddMilliseconds(totalTime).ToString("HH:mm:ss"));

                                //var decoded = dataPart;// U(dataPart, XorKey);
                                //await stream.WriteAsync(decoded, 0, dataPart.Length);
                            }

                            await stream.FlushAsync();

                            part += RefreshUrlsChunkSize;
                        }

                        Console.WriteLine();

                        if (config.Compression)
                        {
                            Console.WriteLine($"Decompressing {ByteSizeToString(outputStream.Length)} file");

                            outputStream.Position = 0;

                            using (var brstream = new BrotliStream(outputStream, CompressionMode.Decompress))
                            {
                                await brstream.CopyToAsync(stream);
                            }
                        }

                        await outputStream.DisposeAsync();

                        Console.WriteLine("File downloaded");
                    }
                }

                sw.Stop();
            }
        }

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

        public static byte[] CompressArray(ulong[] array) => ArraySerealizer(array);

        public static ulong[] DecompressArray(byte[] data) => ArrayDeserealizer(data);

        /*private static ulong GetDeltaMin(ulong[] nums)
        {
            if (nums.Length < 2) return 0;

            ulong min = ulong.MaxValue;

            for (int i = 1; i < nums.Length; i++)
            {
                ulong diff = nums[i] - nums[i - 1];
                if (diff < min) min = diff;
            }

            return min;
        }*/

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

                ulong[] encoded = new ulong[nums.Length];

                for (int i = 0; i < nums.Length; i++)
                {
                    ulong n = nums[i];

                    encoded[i] = n - last;

                    if (i != nums.Length - 1) encoded[i] -= deltaMin;

                    last = n;
                }

                for (int i = 0; i < nums.Length; i++)
                {
                    ulong num = encoded[i];

                    var bytes = BitConverter.GetBytes((long)num);

                    if (i % 2 == 0) Array.Reverse(bytes);

                    memStr.Write(bytes);
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

                    array[i] = num + last;

                    if (i != array.Length - 1) array[i] += deltaMin;

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

        private sealed class ByteConfig
        {
            public bool Compression { get; set; }
            public int VersionNumber { get; set; } = 1;

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