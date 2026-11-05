using DSFiles_Client.CGuis;
using DSFiles_Client.Helpers;
using DSFiles_Shared;
using JSPasteNet;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Terminal.Gui.App;
using static AnsiHelper;
using Application = Terminal.Gui.App.Application;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using File = System.IO.File;

namespace DSFiles_Client
{
    internal static class Program
    {
        private const string URLProtocol = "DSFILES";

        private const string LogsFolder = "logs";

        public static string WebHookFileName = Path.Combine(LogsFolder, "webHook.dat");

        public static string UploadedFiles = Path.Combine(LogsFolder, "uploaded.log");

        private static string Debug = Path.Combine(LogsFolder, "debug.log");

        public static string? API_TOKEN { get; set; }

        private static bool DirSetted = SetCurrentDir();

        public static string ExecutablePath = Process.GetCurrentProcess().MainModule.FileName;

        private static bool SetCurrentDir()
        {
            try
            {
                string currentDir = AppContext.BaseDirectory;

                Directory.SetCurrentDirectory(currentDir);

                string[] paths = [Path.Combine(currentDir, LogsFolder)];

                foreach (var path in paths)
                {
                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);
                }

                string[] files = [(UploadedFiles), (Debug)];

                foreach (var file in files)
                {
                    if (!File.Exists(file))
                        File.WriteAllText(file, "");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return false;
        }

        public static readonly StreamWriter UploadedFilesWriter =
            new StreamWriter(File.Open(UploadedFiles, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };

        public static readonly StreamWriter DebugWriter =
            new StreamWriter(File.Open(Debug, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };

        public static readonly HttpClient client = new HttpClient(new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls13 |
                           System.Security.Authentication.SslProtocols.Tls12,
            CookieContainer = new CookieContainer(),
        })
        {
            Timeout = TimeSpan.FromSeconds(599),
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        public static string GetFromJspaste(string data)
        {
            return JSPasteClient.Get(data.Split('/').Last(), "hola").Result
                .Split('\n')
                .First(l => !string.IsNullOrEmpty(l) && l[0] != '#')
                .Split('/')[0];
        }

        public static void SevenZipPaths(string outputFile, CompressionLevel compressionLevel, params string[] paths)
        {
            if (string.IsNullOrWhiteSpace(outputFile))
                throw new ArgumentException("Output file cannot be null or empty.", nameof(outputFile));

            if (paths == null || paths.Length == 0)
                throw new ArgumentException("No paths provided.");

            bool isWindows = OperatingSystem.IsWindows();

            string programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ??
                                  Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string defaultWinPath = Path.Combine(programFiles, @"7-Zip\7z.exe");
            string? sevenZipExe = Get7ZipPath(isWindows, defaultWinPath);

            if (sevenZipExe == null)
            {
                if (isWindows)
                {
                    Console.WriteLine(AnsiColors.Yellow + "Installing " + AnsiColors.BrightYellow + "7-Zip " +
                                      AnsiColors.Yellow + "please wait..." + AnsiColors.White);

                    var wingetProcess = Process.Start(new ProcessStartInfo("winget",
                            "install --accept-source-agreements --accept-package-agreements 7zip.7zip")
                    {
                        UseShellExecute = true
                    });
                    wingetProcess?.WaitForExit();

                    if (wingetProcess == null || wingetProcess.ExitCode != 0)
                        throw new FileNotFoundException(
                            "Failed to install 7-Zip automatically. Please install it manually.");

                    sevenZipExe = File.Exists(defaultWinPath) ? defaultWinPath : "7z.exe";
                }
                else
                {
                    throw new FileNotFoundException(
                        "7-Zip is not installed. Please install it manually (e.g., 'sudo apt install 7zip').");
                }
            }

            int processors = Environment.ProcessorCount;
            int level = 9;
            int dictSize = 64;
            int wordSize = 192;

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
                case CompressionLevel.NoCompression:
                    level = 0;
                    dictSize = 0;
                    wordSize = 0;
                    break;
            }

            string listFilePath = Path.GetTempFileName();

            try
            {
                if (level > 0)
                {
                    Console.WriteLine(AnsiColors.Orange + "Compressing with dictSize" + AnsiColors.DarkGray + ":" +
                                      AnsiColors.Yellow + dictSize + "m" + AnsiColors.LightGray);
                }

                var validPaths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim('\"'));
                File.WriteAllLines(listFilePath, validPaths, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = sevenZipExe,
                    UseShellExecute = false
                };

                psi.ArgumentList.Add("a");
                psi.ArgumentList.Add("-t7z");
                psi.ArgumentList.Add("-m0=lzma2");
                psi.ArgumentList.Add($"-mx={level}");
                psi.ArgumentList.Add($"-mmt={processors}");
                psi.ArgumentList.Add("-aoa");

                if (level > 0)
                {
                    psi.ArgumentList.Add($"-mfb={wordSize}");
                    psi.ArgumentList.Add($"-md={dictSize}m");
                }

                psi.ArgumentList.Add("-ms=on");
                psi.ArgumentList.Add("-bsp1");
                psi.ArgumentList.Add("-bse1");
                psi.ArgumentList.Add("-bt");

                psi.ArgumentList.Add(outputFile);

                psi.ArgumentList.Add($"@{listFilePath}");

                using var process = Process.Start(psi);
                process?.WaitForExit();

                if (process != null && process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"7-Zip failed with exit code {process.ExitCode}.");
                }
            }
            finally
            {
                Console.Write(AnsiColors.White);

                if (File.Exists(listFilePath))
                {
                    try
                    {
                        File.Delete(listFilePath);
                    }
                    catch
                    {

                    }
                }
            }
        }

        private static string? Get7ZipPath(bool isWindows, string? defaultWinPath)
        {
            if (isWindows && File.Exists(defaultWinPath))
                return defaultWinPath;

            string cmd = isWindows ? "where" : "which";
            string[] targets = isWindows ? new[] { "7z.exe" } : new[] { "7zz", "7z" };

            foreach (var target in targets)
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo(cmd, target)
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    string output = p?.StandardOutput.ReadToEnd().Trim() ?? "";
                    p?.WaitForExit();

                    if (p?.ExitCode == 0 && !string.IsNullOrEmpty(output))
                        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).First();
                }
                catch (Exception ex)
                {
                    WriteException(ref ex);
                }
            }

            return null;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Console.Title = "Dsfiles Manager";

            if (args.Length > 0 && args[0].StartsWith($"{URLProtocol}://", StringComparison.InvariantCultureIgnoreCase))
            {
                args = args[0].Split('/').Skip(2).Select(Uri.UnescapeDataString)
                    .Select(Environment.ExpandEnvironmentVariables).ToArray();

                foreach (var arg in args)
                {
                    var splited = arg.Split('=');

                    if (splited.Length == 2 && splited[0].ToLower() == "token")
                    {
                        API_TOKEN = splited[1];
                    }
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using (var protocolKey = Registry.ClassesRoot.OpenSubKey(URLProtocol, false))
                {
                    bool admin =
                        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

                    if (admin)
                    {
                        string title = "Upload to DSFILES";

                        using (var shellKey = Registry.ClassesRoot.CreateSubKey(@"*\shell\" + URLProtocol))
                        {
                            shellKey.SetValue("", title);
                            shellKey.SetValue("Icon", ExecutablePath);

                            using (var commandKey = shellKey.CreateSubKey("command"))
                            {
                                commandKey.SetValue(string.Empty, "\"" + ExecutablePath + "\" \"%1\"");
                            }
                        }

                        using (var shellKey = Registry.ClassesRoot.CreateSubKey(@"Directory\shell\" + URLProtocol))
                        {
                            shellKey.SetValue("", title);
                            shellKey.SetValue("Icon", ExecutablePath);

                            using (var commandKey = shellKey.CreateSubKey("command"))
                            {
                                commandKey.SetValue(string.Empty, "\"" + ExecutablePath + "\" \"%1\"");
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
                                    commandKey.SetValue(string.Empty, "\"" + ExecutablePath + "\" \"%1\"");
                                }
                            }
                        }
                    }

                    var value = ((string)Registry.ClassesRoot.OpenSubKey(URLProtocol + "\\shell\\open\\command", false)
                        .GetValue(string.Empty, null));

                    if ((protocolKey == null || (value == null || !value.Contains(ExecutablePath))) && !admin)
                    {
                        var exception =
                            (Exception)(new SecurityException("Can't modify url protocol please run as administrator"));

                        Process.Start(new ProcessStartInfo()
                        {
                            FileName = ExecutablePath,
                            Arguments = string.Join(" ", args),
                            UseShellExecute = true,
                            Verb = "runas"
                        });

                        WriteException(ref exception);

#if !DEBUG
                    return;
#endif
                    }
                }
            }

            IApplication app = Application.Create().Init();

            if (args.Length > 0 && args[0] == "ask_upload")
            {
                var upload = new Upload(app);
                app.Run(upload);
            }

            if (!DirSetted)
                throw new IOException("What?");

            if (!Debugger.IsAttached)
            {
                AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(e.ExceptionObject.ToString());
                    Console.ReadKey();
                };
            }

#if DEBUG
            /*if(args.Length ==0)
            {
                var df = Assembly.GetCallingAssembly().Location.Replace(".dll",".exe");

                Process du = Process.Start(new ProcessStartInfo()
                {
                    FileName = df,
                    Arguments = "",

                    RedirectStandardInput = true,
                });

                {
                    fs.CopyTo(du.StandardInput.BaseStream);
                }

                du.StandardInput.BaseStream.Close();

                du.WaitForExit();
                Environment.Exit(0);
            }*/

            if (args.Length > 0 && args[0].StartsWith('/'))
            {
                if (args[0] == "/updateKey")
                {
                    using (FileStream fs = File.Open(".\\key", FileMode.OpenOrCreate, FileAccess.ReadWrite))
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            byte[] data = client
                                .GetByteArrayAsync("https://www.random.org/cgi-bin/randbyte?nbytes=16384&format=f")
                                .Result;

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
#endif

            args = args.Select(arg =>
                    arg.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase) ? GetFromJspaste(arg) : arg)
                .ToArray();

            WebHookHelper? webHookHelper = null;

            try
            {
                webHookHelper = new WebHookHelper(client, File.ReadAllText(WebHookFileName));
            }
            catch
            {
            }

            if (args.Length > 1)
            {
                string[] splited;

                switch (args[0].ToLower())
                {
                    case "-delete":
                        string data = args[1].Split('/').Last();

                        splited = data.Split(':');

                        webHookHelper = new WebHookHelper(client, BitConverter.ToUInt64(splited[1].FromBase64Url()),
                            splited[2]);

                        ulong[] ids =
                            new DiscordFilesSpliter.GorillaTimestampCompressor().Decompress(splited[0].FromBase64Url());

                        Console.WriteLine("Removing file chunks (" + ids.Length + ")");

                        webHookHelper.RemoveMessages(ids).GetAwaiter().GetResult();

                        if (args.Length > 2)
                        {
                            DSServerHelper.RemoveFile(args[2]).GetAwaiter().GetResult();
                        }

                        Thread.Sleep(2000);
                        return;

                    case "-downnload":
                        using (FileStream fs = File.Open(args[2], FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                   FileShare.ReadWrite))
                        {
                            splited = args[1].Split('/')[0].Split(':').Last().Split('$');

                            DiscordFilesSpliter
                                .Decode(splited[0].FromBase64Url(),
                                    (splited.Length > 1 ? splited[1].FromBase64Url() : null), fs).GetAwaiter()
                                .GetResult();
                        }

                        return;

                    case "-upload":

                        using (FileStream fs = File.Open(args[1], FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                   FileShare.ReadWrite))
                        {
                            var encoded = DiscordFilesSpliter.Encode(webHookHelper, Path.GetFileName(args[1]), fs)
                                .GetAwaiter().GetResult();

                            UploadedFilesWriter.WriteLine(encoded.UploadLog);

                            Console.Write("FileSeed: " + encoded.SeedString);
                        }

                        return;

                    case "-pipe":
                        using (FileStream fs = File.Open(args[2], FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                   FileShare.ReadWrite))
                        {
                            splited = args[1].Split('/')[0].Split(':').Last().Split('$');

                            using (var s = DiscordFilesSpliter.DecodeCorePipe(splited[0].FromBase64Url(),
                                       (splited.Length > 1 ? splited[1].FromBase64Url() : null)))
                            {
                                Console.Write("Coping to : " + fs.Handle);

                                s.CopyTo(fs);
                            }
                        }

                        return;
                }
            }

            if (File.Exists(DiscordFilesSpliter.UnsendedIds))
            {
                try
                {
                    string unsendedIds = File.ReadAllText(DiscordFilesSpliter.UnsendedIds);

                    if (!string.IsNullOrWhiteSpace(unsendedIds))
                    {
                        Console.WriteLine("Removing unfinished file upload chunks");

                        webHookHelper
                            .RemoveMessages(unsendedIds.Split('\n').Select(l => l.Trim())
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .Select(l => ulong.TryParse(l, out ulong id) ? id : 0).ToArray()).GetAwaiter()
                            .GetResult();

                        File.WriteAllBytes(DiscordFilesSpliter.UnsendedIds, []);

                        Console.WriteLine();
                    }
                }
                catch (Exception ex)
                {
                    Program.WriteException(ref ex);
                }
            }

            if (args.Length == 0)
            {

                try
                {

                    app.Run<Main>();

                }
                finally
                {
                    app.Dispose();

                }

                return;
            }

            AnsiHelper.InitConsle();

            if (!File.Exists(WebHookFileName))
            {
                string webHookData = app.Clipboard.GetClipboardData();

                if (string.IsNullOrEmpty(webHookData))
                {
                    throw new Exception("Could not get clipboard data");
                }

                string cp = webHookData.Trim();

                if (cp.Contains("https://", StringComparison.InvariantCultureIgnoreCase) &&
                    cp.Contains("webhook", StringComparison.InvariantCultureIgnoreCase))
                {
                    var wh = new WebHookHelper(client, cp);

                    if (wh.channelId <= 0)
                        throw new Exception("WebHook is not valid");

                    File.WriteAllText(WebHookFileName, cp);

                    webHookHelper = new WebHookHelper(client, cp);
                }
                else
                {
                    Console.WriteLine(AnsiColors.BrightRed + "Please " + AnsiColors.Red +
                                      "copy a valid webhook onto the clipboard " + AnsiColors.BrightRed + "first");
                    Console.ReadLine();
                    Environment.Exit(0);
                }
            }

            string filePath = args[0];

            if (args.Length == 1 && Directory.Exists(filePath))
                args = args.Concat([""]).ToArray();

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

                Console.WriteLine(AnsiColors.Blue + "Compressing files please " + AnsiColors.BrightBlue + "wait\n");

                string archivedPath = Path.GetTempFileName();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    ClientHelper.RemoveOnBoot(archivedPath);

                SevenZipPaths(archivedPath, CompressionLevel.Optimal, args);

                filePath = rootPath.Split('\\').Last(c => !string.IsNullOrEmpty(c)) + ".7z";

                int tries = 0;

                while (tries < 5)
                {
                    try
                    {
                        stream = File.OpenRead(archivedPath);
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

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            string fileName = Path.GetFileName(filePath);

            var result = DiscordFilesSpliter.EncodeCore(webHookHelper, fileName, stream, compLevel).Result;

            stream.Dispose();

            if (!string.IsNullOrEmpty(API_TOKEN))
            {
                DSServerHelper.AddFile(result.Json).GetAwaiter().GetResult();
            }

            UploadedFilesWriter.WriteLine(result.UploadLog);

            app.Clipboard.SetClipboardData(result.WebLink);

            Console.WriteLine(AnsiColors.BrightCyan + "WebLink" + AnsiColors.DarkGray + ": " + AnsiColors.Silver +
                              result.WebLink + '\n');
            Console.Write(AnsiColors.BrightBlue + "FileSeed" + AnsiColors.DarkGray + ": " + AnsiColors.Silver +
                          result.DownloadToken);
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
            Console.WriteLine(string.Join('\n', messages.Where(m => !string.IsNullOrEmpty(m))) + '\n' + ex.ToString() +
                              '\n');
            Console.ForegroundColor = lastColor;
        }
    }
}