using DSFiles_Server.Helpers;
using DSFiles_Server.Routes;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Authentication.ExtendedProtection;

namespace DSFiles_Server
{
    internal class Program
    {
        public static HttpClient client = new HttpClient(new HttpClientHandler()
        {
            CookieContainer = new CookieContainer(100),
            AllowAutoRedirect = false,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12,
            MaxConnectionsPerServer = short.MaxValue,
        })
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        private static void Main(string[] args)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\HTTP\Parameters", true))
                    {
                        if (key == null)
                        {
                            Console.WriteLine("Failed to open registry key.");
                            return;
                        }

                        key.SetValue("UrlSegmentMaxLength", ushort.MaxValue / 10, RegistryValueKind.DWord);
                        //key.SetValue("MaxRequestBytes", 1048576, RegistryValueKind.DWord); // 1 MB

                        Console.WriteLine("Registry updated successfully.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An error occurred: {ex.Message}");
                }
            }

            bool debug = Debugger.IsAttached;

            SentrySdk.Init(o =>
            {
                o.Dsn = "https://5a58b08aca581b0c7d6b0ef79aedd518@o4507580376219648.ingest.us.sentry.io/4507580379889664";
                o.Debug = debug;
                o.TracesSampleRate = 1.0;
                o.AddEntityFramework();
            });

            HttpListener listener = new HttpListener() { IgnoreWriteExceptions = false };

            listener.Prefixes.Add("http://*:8080/");
            listener.Start();

            Console.WriteLine("Listening on 8080...");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();
                //HandleContext(context);

                Task.Factory.StartNew(() => HandleContext(context));
            }
        }

        public static async Task HandleContext(HttpListenerContext context)
        {
            HttpListenerRequest req = context.Request;
            HttpListenerResponse res = context.Response;

            res.SendChunked = false;

            res.Headers.Set(HttpResponseHeader.Server, "DSFILES");

            Console.WriteLine($"[{DateTime.Now}] {req.Url.PathAndQuery}");

                switch (req.Url.LocalPath.ToLowerInvariant().Split('/')[1])
                {
                    case "f":
                    case "d":
                    case "df":
                        await DSFilesHandle.HandleFile(req, res);
                        break;

                    case "r":
                    case "rd":
                        await RedirectHandler.HandleRedirect(req, res);
                        break;

                    case "rick":
                        res.Redirect("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
                        res.Close();
                        break;

                    case "download":
                        SpeedTest.HandleDownload(req, res);
                        break;

                    case "upload":
                        SpeedTest.HandleUpload(req, res);
                        break;

                    case "animate":
                        ConsoleAnimation.HandleAnimation(req, res);
                        break;

                    case "favicon.ico":
                        res.SendStatus(404);
                        return;

                    default:
                        res.SendCatError(404);
                        //res.Send("Te perdiste o que señor patata");
                        break;
                }

                res.OutputStream.Close();
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