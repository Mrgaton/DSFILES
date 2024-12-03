using DSFiles;
using DSFiles_Client.Helpers;
using DSFiles_Client.Utils;
using JSPasteNet;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using File = System.IO.File;

namespace DSFiles_Client
{
    internal class Program
    {
        private static bool DirSetted = SetCurrentDir();

        private static bool SetCurrentDir()
        {
            try
            {
                string currentDir = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location);

                Directory.SetCurrentDirectory(currentDir);

                string[] paths = [Path.Combine(currentDir, "logs"), Path.Combine(currentDir, "seeds")];

                foreach (var path in paths)
                {
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                }

                return true;
            }
            catch { }

            return false;
        }

        private const string URLProtocol = "DSFILES";

        private const string WebHookFileName = "logs\\webHook.dat";

        public const string UnsendedIds = "logs\\missing.dat";

        private const string UploadedFiles = "logs\\uploaded.log";

        private const string Debug = "logs\\debug.log";

        public static string? API_TOKEN { get; set; }

        public static readonly StreamWriter UploadedFilesWriter = new StreamWriter(File.Open(UploadedFiles, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        public static readonly StreamWriter DebugWriter = new StreamWriter(File.Open(Debug, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

        public static readonly HttpClient client = new HttpClient(new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12,
            CookieContainer = new CookieContainer()
        });

        private static string FileSeedToString(string fileName, (byte[] seed, byte[]? key, byte[] secret) result, WebHookHelper webHookHelper)
        {
            string extension = Path.GetExtension(fileName).TrimStart('.');
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

            return Encoding.UTF8.GetBytes(fileNameWithoutExtension).BrotliCompress().ToBase64Url()
                + ':' + extension
                + ':' + result.seed.ToBase64Url() + (result.key != null ? '$' + result.key.ToBase64Url() : null) + '/' + result.secret.ToBase64Url()
                + ':' + BitConverter.GetBytes(webHookHelper.id).ToBase64Url()
                + ':' + webHookHelper.token;
        }

        private class Upload
        {
            public string FileName { get; set; }
            public string DownloadToken { get; set; }
            public string Key { get; set; }
            public string RemoveToken { get; set; }
            public string WebLink { get; set; }
            public string? Shortened { get; set; }

            public Upload()
            { }
        }

        private static Upload WriteUploaded(string fileName, (byte[] seed, byte[] key, byte[] secret) result, WebHookHelper webHookHelper)
        {
            var fileSeed = FileSeedToString(fileName, result, webHookHelper);
            var seedSplited = fileSeed.Split('/');

            var upload = new Upload()
            {
                FileName = fileName,
                DownloadToken = seedSplited[0],
                RemoveToken = seedSplited.Last(),
                WebLink = $"https://df.gato.ovh/d/{seedSplited[0].Split(':').Last()}/{HttpUtility.UrlEncode(Encoding.UTF8.GetBytes(fileName))}",
            };

            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"FileName: `{fileName}`");
            sb.AppendLine($"DownloadToken: `{upload.DownloadToken}`");
            sb.AppendLine($"RemoveToken: `{upload.RemoveToken}`");

            upload.Shortened = SendJspaste(fileSeed);

            sb.AppendLine($"Shortened: `{upload.Shortened}`");
            sb.AppendLine($"`WebLink:` {upload.WebLink}");

            if (!string.IsNullOrEmpty(API_TOKEN))
            {
                DSServerHelper.AddFile(fileName,
                    seedSplited[0].Split(':').Last(),
                    upload.RemoveToken,
                    upload.Shortened
                   ).GetAwaiter().GetResult();
            }

            UploadedFilesWriter.WriteLine(sb.ToString());

            string keyString = result.key.ToBase64Url();

            webHookHelper.SendMessageInChunks(string.Join("\n", sb.ToString().Split('\n').Where(l => !l.Contains(keyString)))).GetAwaiter().GetResult();

            return upload;
        }

        private static string SendJspaste(string data)
        {
            var version = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

            try
            {
                return "jsp:/" + JSPasteClient.Publish($"#DSFILES {version}\n\n" + data, new DocumentSettings()
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

        private static string GetFromJspaste(string data)
        {
            return JSPasteClient.Get(data.Split('/').Last(), "hola").Result
               .Split('\n')
               .First(l => !string.IsNullOrEmpty(l) && l[0] != '#')
               .Split('/')[0];
        }

        private static string sevenZipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"7-Zip\");

        private static void SevenZipPaths(string outputFile, CompressionLevel compressionLevel, params string[] paths)
        {
            if (!File.Exists(Path.Combine(sevenZipPath, "7z.dll")))
            {
                Console.WriteLine("Installing 7zip please wait");

                Process.Start(new ProcessStartInfo()
                {
                    FileName = "winget",
                    Arguments = "install --accept-source-agreements --accept-package-agreements 7zip.7zip",
                    CreateNoWindow = false,
                    RedirectStandardError = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    UseShellExecute = false,
                }).WaitForExit();
            }

            int processors = Math.Min(Environment.ProcessorCount, 4);

            int level = 9;
            int wordSize = 192;

            var freeMb = (StreamCompression.AvailableMemory / 1000 / 1000) - 512 - (ulong)wordSize;
            int dictSize = Math.Max(64, (int)freeMb / processors / 5);

            switch (compressionLevel)
            {
                case CompressionLevel.Fastest:
                    level = 2;
                    dictSize = 4;
                    wordSize = 32;
                    break;

                case CompressionLevel.Optimal:
                    level = 6;
                    dictSize = 32;
                    wordSize = 64;
                    break;
            }

            Console.WriteLine("Compressing with dictSize:" + dictSize);

            Process.Start(new ProcessStartInfo()
            {
                FileName = Path.Combine(sevenZipPath, "7z.exe"),
                Arguments = $"a -t7z -m0=lzma2 -mx={level} -mmt={processors} -aoa -mfb={wordSize} -md={dictSize}m -ms=on -bsp1 -bse1 -bt \"{outputFile}\" " + string.Join(' ', paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => '\"' + p.Trim('\"') + '\"')),
            }).WaitForExit();
        }

        [STAThread]
        private static void Main(string[] args)
        {
            //args = ["C:\\Users\\Mrgaton\\Downloads\\King Gnu - SPECIALZ_7765.mp4"];

            if (args.Length > 0 && args[0].StartsWith($"{URLProtocol}://", StringComparison.InvariantCultureIgnoreCase))
            {
                args = args[0].Split('/').Skip(2).Select(Uri.UnescapeDataString).Select(Environment.ExpandEnvironmentVariables).ToArray();

                foreach (var arg in args)
                {
                    var splited = arg.Split('=');

                    if (splited.Length == 2 && splited[0].ToLower() == "token")
                    {
                        API_TOKEN = splited[1];
                    }
                }
            }

            string executablePath = Process.GetCurrentProcess().MainModule.FileName;
            //string shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Path.GetFileNameWithoutExtension(executablePath) + ".lnk");

            using (var protocolKey = Registry.ClassesRoot.OpenSubKey(URLProtocol, false))
            {
                bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

                if (admin)
                {
                    string title = "Upload to DSFILES";

                    using (var shellKey = Registry.ClassesRoot.CreateSubKey(@"*\shell\" + URLProtocol))
                    {
                        shellKey.SetValue("", title);
                        shellKey.SetValue("Icon", executablePath);

                        using (var commandKey = shellKey.CreateSubKey("command"))
                        {
                            commandKey.SetValue(string.Empty, "\"" + executablePath + "\" \"%1\"");
                        }
                    }

                    using (var shellKey = Registry.ClassesRoot.CreateSubKey(@"Directory\shell\" + URLProtocol))
                    {
                        shellKey.SetValue("", title);
                        shellKey.SetValue("Icon", executablePath);

                        using (var commandKey = shellKey.CreateSubKey("command"))
                        {
                            commandKey.SetValue(string.Empty, "\"" + executablePath + "\" \"%1\"");
                        }
                    }

                    using (var protocol = Registry.ClassesRoot.CreateSubKey(URLProtocol))
                    {
                        protocol.SetValue(string.Empty, $"URL: {URLProtocol} Protocol");
                        protocol.SetValue("URL Protocol", string.Empty);

                        using (var protocolOpenKey = protocol.CreateSubKey("shell").CreateSubKey("open"))
                        {
                            using (var commandKey = protocolOpenKey.CreateSubKey("command"))
                            {
                                commandKey.SetValue(string.Empty, "\"" + executablePath + "\" \"%1\"");
                            }
                        }
                    }
                }

                var value = ((string)Registry.ClassesRoot.OpenSubKey(URLProtocol + "\\shell\\open\\command", false).GetValue(string.Empty));

                if ((protocolKey == null || (value == null || !value.Contains(executablePath))) && !admin)
                {
                    var exception = (Exception)(new SecurityException("Can't modify url protocol please run as administrator"));
                    WriteException(ref exception);
                    return;
                }
            }

            /*WshShell shell = new WshShell();
            IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);

            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath);
            shortcut.Description = "Description of your shortcut";
            shortcut.IconLocation = executablePath; // Set the icon to the executable
            shortcut.Save();*/

            //Console.WriteLine('[' + string.Join(", ", args.Select(c => '\"' + c + '\"')) + ']');
            //args = ["upload", "token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpZCI6IjY2OGJkNTA5YzdmZjliOTcxYmUzMzkwNyIsImRhdGEiOiJBQUFBQVEiLCJpYXQiOjE3MjA3MjMzNzh9.eBcyUX7r1oQgX_gPbu6BDmffVVttxam9zSJ_pYQDvP4"];

            if (args.Length > 0 && args[0] == "ask_upload")
            {
                OpenFileDialog ofd = new OpenFileDialog()
                {
                    ValidateNames = false,
                    CheckFileExists = false,
                    CheckPathExists = true,
                    Multiselect = true,
                    DereferenceLinks = true,
                    FileName = "Upload Selection."
                };

                if (ofd.ShowDialog() != DialogResult.OK) return;

                args = ofd.FileNames;
            }

            if (!DirSetted) throw new IOException("What?");

            if (!Debugger.IsAttached)
            {
                AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(e.ExceptionObject.ToString());
                    Console.ReadKey();
                };
            }

            if (args.Length > 0 && args[0].StartsWith('/'))
            {
                if (args[0] == "/updateKey")
                {
                    using (FileStream fs = File.Open(".\\key", FileMode.OpenOrCreate, FileAccess.ReadWrite))
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            byte[] data = client.GetByteArrayAsync("https://www.random.org/cgi-bin/randbyte?nbytes=16384&format=f").Result;

                            Console.WriteLine("Downloaded " + data.Length + " (" + i + ')');

                            if (data.Length < 128)
                            {
                                data = RandomNumberGenerator.GetBytes(16384).Where(d => d != 0).ToArray();

                                Console.WriteLine("Generated localy: " + data.Length);
                            }

                            fs.Write(data, 0, data.Length);
                        }
                    }
                }

                /*if (args[0] == "/train")
                {
                    List<byte[]> data = [];

                    foreach (var file in Directory.EnumerateFiles(args.Length > 1 ? args[1] : "seeds\\", "*", SearchOption.AllDirectories))
                    {
                        data.Add(File.ReadAllBytes(file));
                    }

                    var dict = DictBuilder.TrainFromBufferFastCover(data, 22, 512 * 1024 * 1024);

                    File.WriteAllBytes("trained.dict", dict.ToArray());
                }*/

                return;
            }

            args = args.Select(arg => arg.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase) ? GetFromJspaste(arg) : arg).ToArray();

            //Console.WriteLine(string.Join(" ", args));

            WebHookHelper? webHookHelper = null;

            try
            {
                webHookHelper = new WebHookHelper(File.ReadAllText(WebHookFileName));
            }
            catch { }

            Console.ForegroundColor = ConsoleColor.White;

            if (args.Length > 1)
            {
                if (args[0].Equals("delete", StringComparison.InvariantCultureIgnoreCase))
                {
                    string data = args[1].Split('/').Last();

                    string[] splited = data.Split(':');

                    webHookHelper = new WebHookHelper(BitConverter.ToUInt64(splited[1].FromBase64Url()), splited[2]);

                    ulong[] ids = DiscordFilesSpliter.DecompressArray(splited[0].FromBase64Url().Inflate());

                    Console.WriteLine("Removing file chunks (" + ids.Length + ")");

                    webHookHelper.RemoveMessages(ids).GetAwaiter().GetResult();

                    if (args.Length > 2)
                    {
                        DSServerHelper.RemoveFile(args[2]).GetAwaiter().GetResult();
                    }

                    Thread.Sleep(2000);
                    return;
                }
                else if (args[0].Equals("download", StringComparison.InvariantCultureIgnoreCase))
                {
                    using (FileStream fs = File.Open(args[2], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        string[] splited = args[1].Split('/')[0].Split(':').Last().Split('$');

                        DiscordFilesSpliter.Decode(splited[0].FromBase64Url(), (splited.Length > 1 ? splited[1].FromBase64Url() : null), fs).GetAwaiter().GetResult();
                    }

                    return;
                }
                else if (args[0].Equals("upload", StringComparison.InvariantCultureIgnoreCase))
                {
                    using (FileStream fs = File.Open(args[1], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        var result = DiscordFilesSpliter.Encode(webHookHelper, fs).GetAwaiter().GetResult();

                        var uploaded = WriteUploaded(Path.GetFileName(args[1]), result, webHookHelper);

                        Console.Write("FileSeed: " + uploaded.Shortened ?? uploaded.DownloadToken);
                    }

                    return;
                }
            }

            if (File.Exists(UnsendedIds))
            {
                try
                {
                    string unsendedIds = File.ReadAllText(UnsendedIds);

                    if (!string.IsNullOrWhiteSpace(unsendedIds))
                    {
                        Console.WriteLine("Removing unfinished file upload chunks");

                        webHookHelper.RemoveMessages(unsendedIds.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => ulong.TryParse(l, out ulong id) ? id : 0).ToArray()).GetAwaiter().GetResult();

                        File.WriteAllBytes(UnsendedIds, []);

                        Console.WriteLine();
                    }
                }
                catch (Exception ex)
                {
                    Program.WriteException(ref ex);
                }
            }

            /*byte[] val = Compress(new List<ulong>() { 1163590914585940018, 1163590939156152330, 1163590953945268364, 1163590977127194658, 1163591000413970554, 1163591016801124363, 1163591040540868698, 1163591061541756978, 1163591068487536691, 1163591114377404477, 1163591134988214294, 1163591157318688833, 1163591178676097146, 1163591201757347911, 1163591235089477672, 1163591301082652743, 1163591332040818698 });

            int b = 0;

            foreach (var a in Decompress(val))
            {
                b++;
                Console.WriteLine(a + " " + b);
            }

            SplitByte(255, out byte o, out byte h);*/

            /*Console.WriteLine(o);
            Console.WriteLine(h);*/

            //Console.WriteLine(Combine4Bits(16,13));

            //byte[] data = File.ReadAllBytes("C:\\Users\\Mrgaton\\Downloads\\Windows10_InsiderPreview_Client_x64_es-es_19045.1826.iso");

            // byte[] dataToUpload = File.ReadAllBytes("C:\\Users\\gatoncio\\Downloads\\Windows11_InsiderPreview_Client_x64_es-es_22631.iso");

            //Console.WriteLine(Convert.ToBase64String(dataToUpload).ToString());

            //DiscordFilesSpliter.Decode("AB8AxLihWC8OAmEBjAxyrQ4pIhhGUG8iGSpQACIVIFCCIhQJINUiBaUgeA", "claro2.msi").GetAwaiter().GetResult();

            if (args.Length > 0)
            {
                if (!File.Exists(WebHookFileName))
                {
                    string cp = Clipboard.GetText().Trim();

                    if (cp.Contains("https://", StringComparison.InvariantCultureIgnoreCase) && cp.Contains("webhook", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var wh = new WebHookHelper(cp);

                        if (wh.channelId <= 0) throw new Exception("WebHook is not valid");

                        File.WriteAllText(WebHookFileName, cp);

                        webHookHelper = new WebHookHelper(cp);
                    }
                    else
                    {
                        Console.WriteLine("Please copy a valid webhook first onto the clipboard first");
                        Console.ReadLine();
                        Environment.Exit(0);
                    }
                }

                string filePath = args[0];

                if (args.Length == 1 && Directory.Exists(filePath)) args = args.Concat([""]).ToArray();

                CompressionLevel compLevel = CompressionLevel.NoCompression;

                Stream? stream = null;

                if (args.Length == 1)
                {
                    stream = File.OpenRead(filePath);

                    compLevel = DiscordFilesSpliter.ShouldCompress(Path.GetExtension(filePath), stream.Length);
                }
                else
                {
                    string rootPath = ZipCompressor.GetRootPath(args);

                    if (File.Exists(StreamCompression.tempCompressorPath)) File.Delete(StreamCompression.tempCompressorPath);

                    Console.WriteLine("Compressing files please wait\n");

                    var compressionLevel = DiscordFilesSpliter.ShouldCompress(null, 0, false);

                    SevenZipPaths(StreamCompression.tempCompressorPath, compressionLevel, args);

                    filePath = rootPath.Split('\\').Last(c => !string.IsNullOrEmpty(c)) + ".7z";

                    int tries = 0;

                    while (tries < 5)
                    {
                        try
                        {
                            stream = File.OpenRead(StreamCompression.tempCompressorPath);
                            break;
                        }
                        catch (Exception ex)
                        {
                            tries++;

                            Program.WriteException(ref ex);
                        }
                    }

                    Console.WriteLine();
                }

                if (stream == null) throw new ArgumentNullException(nameof(stream));

                var result = DiscordFilesSpliter.EncodeCore(webHookHelper, stream, compLevel).Result;

                stream.Dispose();

                string fileName = Path.GetFileName(filePath);

                var uploaded = WriteUploaded(fileName, result, webHookHelper);

                try
                {
                    Clipboard.SetText(uploaded.WebLink);
                }
                catch { }

                Console.WriteLine("WebLink: " + uploaded.WebLink + '\n');
                Console.Write("FileSeed: " + uploaded.Shortened);

                Console.ForegroundColor = Console.BackgroundColor;
                Console.ReadLine();
                Environment.Exit(0);
            }

            Console.WriteLine("Write the seed of the file you want to download");
            Console.WriteLine();
            Console.Write("Seed:");

            string fileData = Console.ReadLine().Trim();

            if (fileData.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase)) fileData = GetFromJspaste(fileData); //JSPasteClient.Get(fileData.Split('/').Last()).Result;

            //Console.WriteLine(fileData);

            string[] fileDataSplited = fileData.Split('/')[0].Split(':');

            string[] seedSplited = fileDataSplited.Last().Split('$');

            byte[] seed = seedSplited[0].FromBase64Url();
            byte[] key = seedSplited.Length > 1 ? seedSplited[1].FromBase64Url() : null;

            string destFileName = Encoding.UTF8.GetString(fileDataSplited[0].FromBase64Url().BrotliDecompress()) + (fileDataSplited.Length > 2 && !string.IsNullOrEmpty(fileDataSplited.Skip(1).First()) ? '.' + fileDataSplited.Skip(1).First() : null);

            SaveFileDialog sfd = new SaveFileDialog()
            {
                FileName = destFileName,
                ShowHiddenFiles = true,
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                string filename = sfd.FileName;

                Console.WriteLine();

                using (FileStream fs = File.Open(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    DiscordFilesSpliter.Decode(seed, key, fs).GetAwaiter().GetResult();
                }
            }
            else
            {
                Console.WriteLine("\nOperation cancelled");
                Thread.Sleep(2000);
                Environment.Exit(0);
            }

            //byte[] fileSeed = Convert.FromBase64String("AQMgxMbmo5kPC0BEaaeFEhAUUEQAq4USEB4whI2uhRIQCgDEV7GFEhA=");

            //WFIMEncoder.Decode(fileSeed, "resultado.zip").GetAwaiter().GetResult();

            //Console.WriteLine(Convert.ToBase64String(WFIMEncoder.Decode(fileSeed).Result).ToString());

            Console.ReadLine();
        }

        public static void WriteException(ref Exception ex, params string[] messages)
        {
            QuickWriteException(ref ex, messages);

            Thread.Sleep(5000);
        }

        public static void QuickWriteException(ref Exception ex, params string[] messages)
        {
            var lastColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(string.Join('\n', messages.Where(m => !string.IsNullOrEmpty(m))) + '\n' + ex.ToString() + '\n');
            Console.ForegroundColor = lastColor;
        }
    }
}