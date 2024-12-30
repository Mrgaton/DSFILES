using JSPasteNet;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace DSFiles_Shared
{
    public static class DiscordFilesSpliter
    {
        public const string UnsendedIds = "Missing.dat";
        public static StreamWriter UnsendedIdsWriter { get => new StreamWriter(File.Open(UnsendedIds, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { AutoFlush = true }; }

        /*public static IEnumerable<byte[]> SplitByLength(byte[] bytes, int maxLength)
        {
            for (int index = 0; index < bytes.Length; index += maxLength)
            {
                yield return bytes.Skip(index).Take(Math.Min(maxLength, bytes.Length - index)).ToArray();
            }
        }*/

        //private const long amountPerFile = (25 * 1024 * 1024) - 256;
        private const long amountPerFile = (10 * 1000 * 1000) - 256;

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
        ///
        private static Stopwatch sw = new Stopwatch();

        public static async Task<Upload> Encode(WebHookHelper webHook, string name, Stream stream, CompressionLevel compressionLevel = CompressionLevel.NoCompression) => await EncodeCore(webHook, name, stream, compressionLevel);

        public static async Task<Upload> EncodeCore(WebHookHelper webHook, string name, Stream stream, CompressionLevel compressionLevel = CompressionLevel.NoCompression)
        {
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
                Console.WriteLine("File compressed " + Math.Round((compressedSize / (double)originalFileSize) * 100, 3) + "% compress ratio new size " + ByteSizeToString(compressedSize) + " from " + ByteSizeToString(originalFileSize));
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

                byte[] key = RandomNumberGenerator.GetBytes(16);

                using (TransformStream ts = new TransformStream(uploadStream, key))
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
                            WriteException(ref ex, (response ?? "Uknown").ToString());

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

                return new Upload(name, seedData.ToArray().Deflate(), key, CompressArray(messagesIdsList.ToArray()).Deflate(), ref webHook);
            }
        }

        /// <summary>
        /// Decode part
        /// </summary>
        /// <param name="seed"></param>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public const int RefreshUrlsChunkSize = 50;

        public static async Task Decode(byte[] seed, byte[] key, Stream stream) => await DecodeCore(seed, key, stream);

        public static async Task Decode(string seed, byte[] key, Stream stream) => await DecodeCore(seed.FromBase64Url(), key, stream);

        private static async Task DecodeCore(byte[] seed, byte[] key, Stream stream)
        {
            using (MemoryStream seedData = new MemoryStream(seed.Inflate()))
            {
                ByteConfig config = new ByteConfig((byte)seedData.ReadByte());

                uint sizeInterval = BitConverter.ToUInt32(seedData.ReadAmout(sizeof(uint)));
                ulong channelId = BitConverter.ToUInt64(seedData.ReadAmout(sizeof(ulong)));

                ulong[] attachementsId = DecompressArray(seedData.ReadAmout(seedData.Length - (sizeof(ulong) * 1) - sizeof(bool)));

                int attachements = attachementsId.Length;
                long aproxSize = attachements * amountPerFile;

                Stream? outputStream = config.Compression ? StreamCompression.GetCompressorStream((ulong)aproxSize * 2) : stream;

                Console.WriteLine("Downloading aprox max size " + ByteSizeToString(aproxSize) + '\n');

                string[] attachementsUrls = attachementsId.Select((id, index) => $"https://cdn.discordapp.com/attachments/{channelId}/{id}/{EncodeAttachementName(channelId, index + 1, attachements)}").ToArray();

                sw.Start();

                using (TransformStream ts = new TransformStream(outputStream, key))

                using (HttpClient tempClient = new HttpClient())
                {
                    int part = 0;
                    long downloaded = 0;

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
                                WriteException(ref ex);

                                Thread.Sleep(2000);

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

                sw.Stop();
            }
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

                string fileSeed = Encoding.UTF8.GetBytes(fileNameWithoutExtension).BrotliCompress().ToBase64Url()
                    + ':' + extension
                    + ':' + seed.ToBase64Url() + (key != null ? '$' + key.ToBase64Url() : null)
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

                string keyString = key.ToBase64Url();

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