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

        private static string FileSeedToString(string fileName, byte[] seed, byte[] secret, WebHookHelper webHookHelper)
        {
            string extension = Path.GetExtension(fileName).TrimStart('.');
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

            return Encoding.UTF8.GetBytes(fileNameWithoutExtension).BrotliCompress().ToBase64Url() + ':' + extension + ':' + seed.ToBase64Url() + '/' + secret.ToBase64Url() + ':' + BitConverter.GetBytes(webHookHelper.id).ToBase64Url() + ':' + webHookHelper.token;
        }
        private class Upload
        {
            public string FileName { get; set; }
            public string DownloadToken { get; set; }
            public string RemoveToken { get; set; }
            public string WebLink { get; set; }
            public string? Shortened { get; set; }
            public Upload() { }
        }
        private static Upload WriteUploaded(string fileName, byte[] seed, byte[] secret, ulong size, WebHookHelper webHookHelper)
        {
            var fileSeed = FileSeedToString(fileName, seed, secret, webHookHelper);
            var seedSplited = fileSeed.Split('/');

            var upload = new Upload()
            {
                FileName = fileName,
                DownloadToken = seedSplited[0],
                RemoveToken = seedSplited.Last(),
                WebLink = $"https://df.gato.ovh/d/{seedSplited[0].Split(':').Last()}/{HttpUtility.UrlEncode(Encoding.UTF8.GetBytes(fileName))}",
            };

            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"`FileName:` {fileName}");
            sb.AppendLine($"`DownloadToken:` {upload.DownloadToken}");
            sb.AppendLine($"`RemoveToken:` {upload.RemoveToken}");
            sb.AppendLine($"`WebLink:` {upload.WebLink}");

            upload.Shortened = SendJspaste(fileSeed);

            sb.AppendLine($"`Shortened:` {upload.Shortened}");

            if (!string.IsNullOrEmpty(API_TOKEN))
            {
                DSServerHelper.AddFile(fileName,
                    seedSplited[0].Split(':').Last(),
                    upload.RemoveToken,
                    upload.Shortened,
                    size).GetAwaiter().GetResult();
            }

            UploadedFilesWriter.WriteLine(sb.ToString());

            webHookHelper.SendMessageInChunks(sb.ToString()).GetAwaiter().GetResult();

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

                    using (var key = Registry.ClassesRoot.CreateSubKey(@"*\shell\" + URLProtocol))
                    {
                        key.SetValue("", title);
                        key.SetValue("Icon", executablePath);

                        using (var commandKey = key.CreateSubKey("command"))
                        {
                            commandKey.SetValue(string.Empty, "\"" + executablePath + "\" \"%1\"");
                        }
                    }

                    using (var key = Registry.ClassesRoot.CreateSubKey(@"Directory\shell\" + URLProtocol))
                    {
                        key.SetValue("", title);
                        key.SetValue("Icon", executablePath);

                        using (var commandKey = key.CreateSubKey("command"))
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

            /*string d = "TJh5fBXVFcfPGybhxVrPIUFrJXCvLRBqP9QAajXI82UYHtaFzSIoUCUI2koQNwooYZhMngtQBbdAbRFBrLK6EaryMpkMT4FQtBCgmwmuYbFaErCLmdvXfnJufP_k8825c-6de88953cGLgHribxPNhU8WnTvCBMAjC937uyR-9vr4bbOlCnXtqn6z6Hrt8TEqJicEqOLv0phcwcG-9keOdhYRkmPeaeFzbNk9j3m-wBTFWD9lXmdiUfrhFPE_sIkFM6n4NkujI22xMJK8Dp4_BZDNMA3fo-Fcsha1Tib-d4EDZlE5vnMTxtixUBIZpkDS57Vjmmf_U8xsHOzGpFmHpSVJb-kzDU8_je7lPoHwu-YayzROJaS7zN75WquEqn5zDKi0iJwXfY3NoVfF5EjYl3_eNXEVaWUXMrjP_FlYT_w3ubxF9p4-G-Y0Ou90sVgHBkR87wQ9xaT35P9PZIvz92jYtPYPjohCxCs19ifnU9XfYbOLrb_1JF929Ac2_V8rFqpprgMDrP9snIV2JTm84qNM6j3M8L6OfOsKiiOU3CSx7elaerTGPD7xPpYFC-UzkddbJg-Hq-A7Ele73UJTB9H8zl-fowhhyuEZmbfwo4TauRvmdMenurExGB-viaf3p4nElezfZqH_74IgpXMG0G8XAKpqcwVFgadGOnzqw9paB8JP2J_95SrzuMirdg-O6KCKyBYynbPE9EG1biT7X93sWUURW8xz3JF5161ZD2__9KA-kpKtbB9K2AmEtaHzGtMsAsoc4a5xcQTJP2b-fnyABsCVT6F7StNWjCYYBKvZ6WBTVtEWM7nd4lLpTtU-RP8vOeL0zPIelbHQwhF02VyIvNqA2sfVA2dzNdlqWIcOTOYXwW82yGb9zc23cQbZkinle1_BFy1lKwlzBPSuL4FYTNzg0OFh9BMMf_FoLnL0Khm3m1D5WPCWsbxsTJJlZvRX6_vZ4qGtqpFlzH3tqlnozC-w--_KaABZQS8vzEKYGAjmnt5_BsB9WkWpo6nygQeGk3BQ8ztHhW5qn4u81NZET0K0SLmv7q4bx9mVe8u__0DOb2Y3Fq2v1WvVo0nT9_fCb5oeB69F3g9eQ48VYNJzi9wOqB4JLJPMw_zMSoEW5_H2xka2izyr-TzXGRB2UcYvcz2O218bjnZ-jxv88XaODnnMD_q0Q-Wo_ESsxNC4TIZvcg8J_e-C9HX8f9CD3WmlMJnmOcniR7D5HQdj1k5tgAyd3Wfn7zgBTWS_cX6J2nCS8KqY_sRlyr3o6nP53Ebdw8iR-foTQ5-HaG3gnm_jXUzwfkH85cmftCPjAPsf3BCbLyZgtNs7yxXtTMofzvz3wDXTZF2Px3fPtbeRvYmHZ8-vlFI9hr297SLn55NmT-wvcmA-S2YXsz2azOiYz1ae5i_bdCMOgx0Pq02cXOkFr_CvC2fSkeRexPzixYeakBH15-PgPr1otSjzHcY1KdYRguZH-yhti2koB-fN1g4u1AG77D9HQMKb5BgM09pVHOGymQl83ELPysA60_MQ23RvgDMScz_dLC5BgOdbz7w5fclWbr-vmJgXRzMn_D7VgY0ZoD0gc_vpRq14QqZuZj5lRBXTaHkYB4_2QNqQudL5lJXXrkNzX3MpgV3HBLmBObnFqmjzZhdzfNfUCUzPoY8Hmp9vL0YDB2PT4Zy9FFMDmcutfFAEXlvsr_C3P605-y8vhWh7FlE_lTezx71qnUsudr_rwMx1YX8a_V9jHD1jTL1d_a328UPfTVC36-LLTl7l2rQ9WNNQFYFhXo_59g4-LfS1_XppIn-MKoq4vn7l6upx9DleIZVWey4TVYVM9-TxahTlRfy-ltdXFdKBusFeDcph-xAP5_tC1LkNGLVDraHBralKNT65XzA9ocw1Pft5sWqrZSyOp_0rxKHSsmdpeerUU0ROuN5_AFbDrqcEjo_HsnSsGfUklPMjydl8Rjwl2v_rrhlOKSf0vfVogu2IHzK9kdsGh5hZgLP97EtWgK0k3o9Fsx8QwSD2H4gA_YOtHX9HmWK1kFgbWb7U0kqbRWOzoc782lMsyqXej9AbKsmS9-fZgudONnXM681oOI9EX3M_NUo9XV_cnT-fdMXD88AvX8xw5N5ESa_x_NXAb7QgvYW5rdtGjuZ3F7MtyoV9Zbu73X-tXHfu2rERubpBjZWSov3Cw576Llk3q_rj4WzZkKg9cSvAnljjVIhr-cSTw4_ra7S-echC-siDHV-mpjEE71kwPoBXKBJT2I6zvu9PUHYrnY5zJe5cvwmtBz2n7KooE3F9PjLLCouoOiYvo_52GDL4KDW0xFNaFKjJvL4vIy8vA5dbX82S_Y0DO7k_TlhYdSBVawXY7vKVZSSWt-DSlHxXnR1vt-Q0w9xCnczf9GgjgVovcq8z6HqzxA4X8PngRw2BdPr9PNJ2rgRnVKe_0iAW2ZD-IW-X6746hh6uj4erVcdzaI7XyxwIJdPQp1vPg9E3SWU5HoXe8CigeeRqevFRB-3TSZT1997F6vTJ0X-tTz_JIPGVqty1v-xqxzR1IdgANsDD0_OIUfng1_XqOh19At4fCYpclrEdXT8e7KoU8V0fF3h4-7bKDuO-6f3fbGnHyRZL8bO8jB9Sth6_xyf4jMp1Of97QjytqJ9hOefZ-HxXtI0mWd2qtYyaXB-iP3QpFGNaPN5xw5n8OAacDk_xs5NiWklFDyo82cCUh9g6gH21-Tg5t4yuKaLjVzA9omTVaL7OQPX3STDW3T_V65O_1ImSOt_Gxf8APyR2p-HB1vR0Pqt1sDmuLR0_zXSgPgQGTzA9jM9VNRXJhfy-8_N3d89IqX1zPYkVGwTod6vKEMDalSefp-XkvLmYrIe1_2Ki8f2qR5ar98XYedNMs16L9bTxz3rMcn6ODbEhFQufu7h8ee58pxapXQ--Z1DZUcxvVbfP0NWHsGE1rOfmnTOewjHdX1Jw8S0SPxF77-HJ_LBeJ55oI3RQIq-z_4_ycpBz2MV31_YnoWBASZuYF5k_a-_D_ozv2aJE9Ohu3_Zny_2Xw-6n4otd7G9QRjju-IvtiLXv40Ce5quXw7uXY-h1i8rDenk6peO3zNJvKMQTF1v2yzZfyv6u7vzIc19QzV-V-ffXD34GoN5Ws86GJ0rwz8z70jixr4Q3qf1oiGOH0VH97dOFk-VgDWE_S1JyKnXQHA1xwOl8MXPRNCu86uBNaVgaP2mfGwqk2aCn2-oonH_UuUZ3R96-JtBZM_vzg9iUxy8N3W_XiWHtGJ3_QaLKh5T9QPZrmyKdwhnBPP4gKBN5XH9i10UyCsPon-C-eyAzsoHi-tvrBfQ8GJK38LPv5mg-B_Q1f3eL9KybComdH_6skX3b0Vri-7fUtSrNzn6Puw2oLinNOu68yfW5Oq77reOZWH2cUxs0Po6xJOF5Obxfvbx5ICcPjjK6_tug-oYKbv13398KtqM3tU6_gwqzdUf3W9dmqfWedLi-I-dE1PLjwnrUh5fa8HSXL3R_dbjjogiVa37-zWOjM8DR-uhzpRYn4KqYWxvc_F5m_JZ_8YmZWj4FpGdpc87p6fLwLudx9dX4ZZ5MqW_x0wG3DCQQu1vq0-F7aq6P9-HCwLsbBKO_n6w04Gr24RxNo-_w6XLvwWWzh9PmmIaEaQ5P87NUolN0f28viUeTD9fOF_oft-l2YcxvErrOxviAWb0fVkRSKcGs1o_tgBNOYDeqW79hIcvBvtW5rsTElrR3PCN_swvA6Mvz48ZcagIUlofvJuhkQXS1PrxA6VUrfC-7P7eAmO2Clim9ZZNFSPA0fXzOkOkTXJ1fX7dhTGHhHWL_h5QrVYvlZbWVy2L1OnrpafP-9RI9dOjmJnfo8vfnFCu2aNiul9cbuLquOz-_tDmU2oAhPVaLwZ4phVBn_dOF5f9hPwf8_6NTNOFJRRpPb_BwNXHROpWrZd8KikAe7H-_pTE7R1os16ODXVpzGQZjdb6LIurFTq6X37dxM4S8HW--pknv_UuWpy_YRLQXTtUfb-enM8CmQfg6fV8FeKOgyLx__4zt_XqwTwV_Q";

            var encoded = d.FromBase64Url();

            MemoryStream ms = new MemoryStream(encoded.Inflate());

            byte[] buffer = new byte[ms.Length - (1 + 8+  8)];
            ms.Position = 8 + 8 + 1;
            ms.Read(buffer, 0, buffer.Length);

            ulong[] dids = DiscordFilesSpliter.DecompressArray(buffer);

            //dids = dids.Select(d => d - dids[0]).ToArray();

            Console.WriteLine(string.Join(',',dids));

            var rawdata = dids.SelectMany(d => BitConverter.GetBytes(d )).ToArray();

            rawdata = DiscordFilesSpliter.CompressArray(dids);

            var modArrayDeflate = buffer.Deflate();
            var normalDeflate = rawdata.Deflate();

            var modArrayBrotli = buffer.BrotliCompress();
            var normalBrotli = rawdata.BrotliCompress();

            var zstd = new Compressor(Compressor.MaxCompressionLevel);

            var modArrayZSTD = zstd.Wrap(buffer);

            zstd.LoadDictionary(File.ReadAllBytes("trained.dict"));
            zstd.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_compressionLevel, 22);

            var normalZSTD = zstd.Wrap(rawdata);

            return;*/

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
                        DiscordFilesSpliter.Decode(args[1].Split('/')[0].Split(':').Last().FromBase64Url(), fs).GetAwaiter().GetResult();
                    }

                    return;
                }
                else if (args[0].Equals("upload", StringComparison.InvariantCultureIgnoreCase))
                {
                    using (FileStream fs = File.Open(args[1], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        var result = DiscordFilesSpliter.Encode(webHookHelper, fs).GetAwaiter().GetResult();

                        var uploaded = WriteUploaded(Path.GetFileName(args[1]), result.seed, result.secret, result.size, webHookHelper);

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

                var uploaded = WriteUploaded(fileName, result.seed, result.secret, result.size, webHookHelper);

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

            byte[] seed = fileDataSplited.Last().FromBase64Url();

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
                    DiscordFilesSpliter.Decode(seed, fs).GetAwaiter().GetResult();
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
            var lastColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(string.Join('\n', messages.Where(m => !string.IsNullOrEmpty(m))) + '\n' + ex.ToString() + '\n');
            Console.ForegroundColor = lastColor;
            Thread.Sleep(5000);
        }
    }
}