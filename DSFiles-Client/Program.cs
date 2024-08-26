using DSFiles;
using JSPasteNet;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security;
using System.Security.AccessControl;
using System.Security.Permissions;
using System.Security.Principal;
using System.Text;
using System.Web;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace DSFiles_Client
{
    internal class Program
    {
        private static bool DirSetted = SetCurrentDir();

        private static bool SetCurrentDir()
        {
            try
            {
                Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetCallingAssembly().Location));

                return true;
            }
            catch
            {
                return false;
            }
        }

        private const string URLProtocol = "DSFILES";

        private const string WebHookFileName = "WebHook.dat";

        public const string UnsendedIds = "Missing.dat";

        private const string UploadedFiles = "Uploaded.log";

        public static string? API_TOKEN = null;

        private static StreamWriter UploadedFilesWriter = new StreamWriter(File.Open(UploadedFiles, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));

        private static string FileSeedToString(string fileName, byte[] seed, byte[] secret, WebHookHelper webHookHelper)
        {
            string extension = Path.GetExtension(fileName).TrimStart('.');
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

            return Encoding.UTF8.GetBytes(fileNameWithoutExtension).BrotliCompress().ToBase64Url() + ':' + extension + ':' + seed.ToBase64Url() + '/' + secret.ToBase64Url() + ':' + BitConverter.GetBytes(webHookHelper.id).ToBase64Url() + ':' + webHookHelper.token;
        }

        private static string WriteUploaded(string fileName, byte[] seed, byte[] secret, ulong size, WebHookHelper webHookHelper)
        {
            var fileSeed = FileSeedToString(fileName, seed, secret, webHookHelper);
            var seedSplited = fileSeed.Split('/');

            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"`FileName:` {fileName}");
            sb.AppendLine($"`DownloadToken:` {seedSplited[0]}");
            sb.AppendLine($"`RemoveToken:` {seedSplited.Last()}");
            sb.AppendLine($"`WebLink:` https://df.gato.ovh/df/{seedSplited[0].Split(':').Last()}/{HttpUtility.UrlEncode(Encoding.UTF8.GetBytes(fileName))}");

            //sb.AppendLine($"`DownloadLink:` https://df.gato.ovh/df/{fileSeed.Split('/')[0]}");

            string jspasteSeed = SendJspaste(fileSeed);

            sb.AppendLine($"`Shortened:` {jspasteSeed}");

            DSServerHelper.AddFile(fileName,
                seedSplited[0].Split(':').Last(),
                seedSplited.Last(),
                jspasteSeed,
                size).GetAwaiter().GetResult();

            //UploadedFilesWriter.BaseStream.Position = UploadedFilesWriter.BaseStream.Length - 1;
            UploadedFilesWriter.WriteLine(sb.ToString());
            UploadedFilesWriter.Flush();

            webHookHelper.SendMessageInChunks(sb.ToString()).GetAwaiter().GetResult();

            return jspasteSeed;
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
                    Secret = "porrosricos"
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

            using (var protocolKey = Registry.ClassesRoot.OpenSubKey(URLProtocol, true))
            {
                bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

                if (admin && protocolKey == null)
                {
                    using (var protocol = Registry.ClassesRoot.CreateSubKey(URLProtocol))
                    {
                        protocol.SetValue(string.Empty, $"URL: {URLProtocol} Protocol");
                        protocol.SetValue("URL Protocol", string.Empty);

                        using (var protocolShellKey = protocol.CreateSubKey("shell"))
                        {
                            protocolShellKey.CreateSubKey("open");
                        }

                        RegistrySecurity security = new RegistrySecurity();

                        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                            RegistryRights.FullControl,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));

                        protocol.SetAccessControl(security);
                    }
                }

                if (protocolKey == null && !admin)
                {
                    var exception = (Exception)(new SecurityException("Can't create registry url protocol please run as administrator"));
                    WriteException(ref exception);
                    return;
                }

                using (var commandKey = Registry.ClassesRoot.OpenSubKey($"{URLProtocol}\\shell\\open", true).CreateSubKey("command"))
                {
                    commandKey.SetValue(string.Empty, Process.GetCurrentProcess().MainModule.FileName + " %1");
                }
            }

            //Console.WriteLine('[' + string.Join(", ", args.Select(c => '\"' + c + '\"')) + ']');
            //args = ["upload", "token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpZCI6IjY2OGJkNTA5YzdmZjliOTcxYmUzMzkwNyIsImRhdGEiOiJBQUFBQVEiLCJpYXQiOjE3MjA3MjMzNzh9.eBcyUX7r1oQgX_gPbu6BDmffVVttxam9zSJ_pYQDvP4"];

            if (args.Length > 0 && args[0] == "upload")
            {
                OpenFileDialog ofd = new OpenFileDialog();

                ofd.ValidateNames = false;
                ofd.CheckFileExists = false;
                ofd.CheckPathExists = true;
                ofd.Multiselect = true;
                ofd.DereferenceLinks = true;
                ofd.FileName = "Upload Selection.";

                if (ofd.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

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

            if (args.Length > 0 && args[0] == "/updateKey")
            {
                using (FileStream fs = File.Open(".\\key", FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    using (WebClient wc = new WebClient())
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            byte[] data = wc.DownloadData("https://www.random.org/cgi-bin/randbyte?nbytes=16384&format=f");

                            Console.WriteLine("Downloaded " + data.Length + " (" + i + ')');

                            fs.Write(data, 0, data.Length);
                        }
                    }
                }

                return;
            }

            /*Console.WriteLine("Compressing please wait");

            SevenZipPaths(ZipCompressor.GetRootPath(args).Split('\\').Last(c => !string.IsNullOrEmpty(c)) + ".7z", args);*/

            /*Console.ReadLine();
            Environment.Exit(0);

            //args = ["C:\\Users\\mrgaton\\Downloads\\Casanova.157,88 Kbit_s.mp3"];

            /*var rer =  File.ReadAllBytes("tempStream.tmp").Decompress();

             Environment.Exit(0);*/

            args = args.Select(arg => arg.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase) ? GetFromJspaste(arg) : arg).ToArray();

            //Console.WriteLine(string.Join(" ", args));

            WebHookHelper? webHookHelper = null;

            /*Console.WriteLine(BitConverter.ToString(new byte[] { 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x23, 0x24, 0x25 }.Compress()));
            Console.WriteLine(BitConverter.ToString(new byte[] {0x22,0x22, 0x22, 0x22, 0x23, 0x24, 0x25 }.Compress().Decompress()));
            Console.WriteLine();

            Console.WriteLine(BitConverter.ToString(Encoding.UTF8.GetBytes("sadsdaiofdsaiufahdsofhajodsfoghaqwe8obr vqatw48r").Compress()));
            Console.WriteLine(BitConverter.ToString(Encoding.UTF8.GetBytes("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").Compress()));
            Console.WriteLine();

            var rand = new Random();

            byte[] buffer = new byte[300];
            rand.NextBytes(buffer);
            Console.WriteLine(BitConverter.ToString(buffer.Compress()));
            Console.WriteLine();

            buffer = new byte[500];
            rand.NextBytes(buffer);
            Console.WriteLine(BitConverter.ToString(buffer.Compress()));
            Console.WriteLine();

            buffer = new byte[32];
            rand.NextBytes(buffer);
            Console.WriteLine(BitConverter.ToString(buffer.Compress()));

            Console.WriteLine();
            Console.WriteLine();
            Console.ReadLine();*/

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
                    DiscordFilesSpliter.Decode(args[1].Split('/')[0].Split(':').Last(), args[2]).GetAwaiter().GetResult();

                    return;
                }
                else if (args[0].Equals("upload", StringComparison.InvariantCultureIgnoreCase))
                {
                    var result = DiscordFilesSpliter.Encode(webHookHelper, args[1]).GetAwaiter().GetResult();

                    string jspLink = WriteUploaded(args[1], result.seed, result.secret, result.size, webHookHelper);

                    Console.Write("FileSeed: " + jspLink);
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

                        if (wh.channelId <= 0) throw new Exception("WebHook not valid");

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
                Stream stream = null;

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
                    //ZipCompressor.CompressZip(ref stream, rootPath, args);

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

                string fileSeed = WriteUploaded(fileName, result.seed, result.secret, result.size, webHookHelper);

                try
                {
                    Clipboard.SetText(fileSeed);
                }
                catch { }

                Console.Write("File seed: " + fileSeed);

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

            string seed = fileDataSplited.Last();
            string destFileName = Encoding.UTF8.GetString(fileDataSplited[0].FromBase64Url().BrotliDecompress()) + (fileDataSplited.Length > 2 ? '.' + fileDataSplited.Skip(1).First() : null);

            /*if (destFileName.EndsWith(".zip",StringComparison.InvariantCultureIgnoreCase))
            {
                SaveFileDialog sfd = new SaveFileDialog()
                {
                    FileName = "YourFolderName", // Predefined folder name
                    Filter = "Directories|*.this.directory", // Choose any extension you want
                    InitialDirectory = @"C:\Your\Initial\Directory" // Initial directory
                };

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    // Now here's our save folder
                    string savePath = Path.GetDirectoryName(sfd.FileName);
                    // Do whatever

                    Console.WriteLine(savePath);
                    Console.ReadLine();
                }
            }*/

            SaveFileDialog sfd = new SaveFileDialog()
            {
                FileName = destFileName,
                ShowHiddenFiles = true,
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                string filename = sfd.FileName;

                Console.WriteLine();

                DiscordFilesSpliter.Decode(seed, filename).GetAwaiter().GetResult();
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

        public static HttpClient client = new HttpClient();

        public static bool CheckFolderPermissions(string folderPath, FileIOPermissionAccess permission = FileIOPermissionAccess.Write)
        {
            var permissionSet = new PermissionSet(PermissionState.None);
            var neededPermission = new FileIOPermission(permission, folderPath);
            permissionSet.AddPermission(neededPermission);
            return permissionSet.IsSubsetOf(AppDomain.CurrentDomain.PermissionSet);
        }

        public static void WriteException(ref Exception ex, params string[] messages)
        {
            var lastColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(string.Join('\n', messages) + '\n' + ex.ToString() + '\n');
            Console.ForegroundColor = lastColor;
            Thread.Sleep(2000);
        }
    }
}