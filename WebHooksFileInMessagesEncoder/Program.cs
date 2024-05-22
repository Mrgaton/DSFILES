using DSFiles;
using JSPasteNet;
using System.Reflection;
using System.Text;

namespace WebHooksFileInMessagesEncoder
{
    internal class Program
    {
        private static bool DirSetted = SetCurrentDir();

        private static bool SetCurrentDir()
        {
            try
            {
                Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

                return true;
            }
            catch
            {
                return false;
            }
        }

        private const string WebHookFileName = "WebHook.txt";

        public const string UnsendedIds = "Missing.txt";

        private const string UploadedFiles = "Uploaded.txt";

        private static StreamWriter UploadedFilesWriter = File.AppendText(UploadedFiles);

        private static string FileSeedToString(string fileName, byte[] seed, byte[] secret, WebHookHelper webHookHelper) => Encoding.UTF8.GetBytes(fileName).ToBase64Url() + ":" + seed.ToBase64Url() + '/' + secret.ToBase64Url() + ':' + BitConverter.GetBytes(webHookHelper.id).ToBase64Url() + ':' + webHookHelper.token;

        private static string WriteUploaded(string fileName, byte[] seed, byte[] secret, WebHookHelper webHookHelper)
        {
            var fileSeed = FileSeedToString(fileName, seed, secret, webHookHelper);

            webHookHelper.SendMessage($"FileName: `{fileName}`", Application.ProductName, "").GetAwaiter().GetResult();
            webHookHelper.SendMessage($"DownloadToken: `{fileSeed.Split('/')[0]}`", Application.ProductName, "").GetAwaiter().GetResult();
            webHookHelper.SendMessage($"RemoveToken: `{fileSeed.Split('/').Last()}`", Application.ProductName, "").GetAwaiter().GetResult();

            return fileSeed;
        }

        private static string SendJspaste(string data)
        {
            try
            {
                return "jsp:/" + JSPasteClient.Publish(data, new DocumentSettings()
                {
                    LifeTime = TimeSpan.MaxValue,
                    KeyLength = 4,
                    Password = "hola"
                }).Result.Key;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return data;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            //args = ["C:\\Users\\mrgaton\\Downloads\\Casanova.157,88 Kbit_s.mp3"];

            /*AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(e.ExceptionObject.ToString());
                Console.ReadKey();
            };*/

            /*var rer =  File.ReadAllBytes("tempStream.tmp").Decompress();

             Environment.Exit(0);*/

            if (!DirSetted) throw new Exception("What?");

            args = args.Select(arg => arg.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase) ? JSPasteClient.Get(arg.Split('/').Last()).Result.Split('/')[0] : arg).ToArray();

            //Console.WriteLine(string.Join(" ", args));

            WebHookHelper webHookHelper = null;

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

            string fileNameLengh = new string('a', 53);

            if (args.Length > 1)
            {
                if (args[0].Equals("delete", StringComparison.InvariantCultureIgnoreCase))
                {
                    string data = args[1].Split('/').Last();
                    string[] splited = data.Split(':');

                    webHookHelper = new WebHookHelper(BitConverter.ToUInt64(splited[1].FromBase64Url()), splited[2]);

                    ulong[] ids = DiscordFilesSpliter.DecompressArray(splited[0].FromBase64Url());

                    Console.WriteLine("Removing file chunkks (" + ids.Length + ")");
                    webHookHelper.RemoveMessages(ids).GetAwaiter().GetResult();
                }
                else if (args[0].Equals("download", StringComparison.InvariantCultureIgnoreCase))
                {
                    DiscordFilesSpliter.Decode(args[1].Split('/')[0].Split(':').Last(), args[2]).GetAwaiter().GetResult();
                }
                else if (args[0].Equals("upload", StringComparison.InvariantCultureIgnoreCase))
                {
                    var result = DiscordFilesSpliter.Encode(webHookHelper, args[1]).GetAwaiter().GetResult();

                    WriteUploaded(args[1], result.seed, result.secret, webHookHelper);

                    string jspLink = SendJspaste(FileSeedToString(args[1], result.seed, result.secret, webHookHelper));
                    UploadedFilesWriter.WriteLine(args[1] + "; " + jspLink);
                    UploadedFilesWriter.Flush();

                    Console.WriteLine("FileSeed: " + jspLink);
                }

                Environment.Exit(0);
            }

            if (File.Exists(UnsendedIds))
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

                var stream = File.OpenRead(filePath);

                var result = DiscordFilesSpliter.EncodeCore(webHookHelper, stream, DiscordFilesSpliter.ShouldCompress(Path.GetExtension(filePath), stream.Length)).Result;

                string fileName = Path.GetFileName(filePath);

                string fileSeed = WriteUploaded(fileName, result.seed, result.secret, webHookHelper);

                UploadedFilesWriter.WriteLine(fileName + "; " + fileSeed);
                UploadedFilesWriter.Flush();

                var jspLink = SendJspaste(fileSeed);

                UploadedFilesWriter.WriteLine(fileName + "; " + jspLink);
                UploadedFilesWriter.Flush();

                Console.WriteLine("File seed: " + jspLink);

                if (args.Length < 2)
                {
                    Console.ForegroundColor = Console.BackgroundColor;
                    Console.ReadLine();
                }

                Environment.Exit(0);
            }

            Console.WriteLine("Write the seed of the file you want to download");
            Console.WriteLine();
            Console.Write("Seed:");

            string fileData = Console.ReadLine().Trim();

            if (fileData.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase)) fileData = JSPasteClient.Get(fileData.Split('/').Last()).Result;

            //Console.WriteLine(fileData);

            string[] fileDataSplited = fileData.Split('/')[0].Split(':');

            string seed = fileDataSplited[1];
            string destFileName = Encoding.UTF8.GetString(fileDataSplited[0].FromBase64Url());

            SaveFileDialog sfd = new SaveFileDialog()
            {
                FileName = destFileName,
                ShowHiddenFiles = true,
            };

            Application.EnableVisualStyles();

            DialogResult dr = sfd.ShowDialog();

            if (dr == DialogResult.OK)
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

        private static HttpClient client = new HttpClient();
    }
}