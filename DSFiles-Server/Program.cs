using DSFiles_Server.Helpers;
using DSFiles_Server.Routes;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DSFiles_Server
{
    internal class Program
    {
        public static HttpClient client = new HttpClient(new HttpClientHandler()
        {
            CookieContainer = new CookieContainer(80),
            AllowAutoRedirect = false,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12,
            MaxConnectionsPerServer = short.MaxValue,
        })
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        private static void Main(string[] args)
        {
            if (File.Exists(".env"))
            {
                foreach (var line in File.ReadAllLines(".env").Select(l => l.Trim()))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                        continue;

                    int separatorIndex = line.IndexOf('=');

                    if (separatorIndex == -1)
                        continue;

                    string key = line.Substring(0, separatorIndex).ToUpper();

                    string value = line.Substring(separatorIndex + 1);

                    Environment.SetEnvironmentVariable(key, value);
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\HTTP\Parameters", true))
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

            if (!debug)
            {
                SentrySdk.Init(o =>
                {
                    o.Dsn = "https://5a58b08aca581b0c7d6b0ef79aedd518@o4507580376219648.ingest.us.sentry.io/4507580379889664";
                    o.Debug = debug;
                    o.TracesSampleRate = 1.0;
                    o.AddEntityFramework();
                });
            }

            HttpListener listener = new HttpListener() { IgnoreWriteExceptions = false };

            listener.Prefixes.Add("http://*:8080/");
            listener.Prefixes.Add("http://localhost:9006/");
            listener.Start();

            Console.WriteLine("DSFILES listening on 8080...");

            Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    try
                    {
                        Thread.Sleep(1 * 60 * 1000);

                        GC.Collect();
                    }
                    catch { }
                }
            });

            while (true)
            {
                HttpListenerContext context = listener.GetContext();
                //HandleContext(context);

                if (context.Request.IsWebSocketRequest)
                {
                    Task.Factory.StartNew(async () => WebSocketHandler.HandleWebSocket(await context.AcceptWebSocketAsync(null)));
                }
                else
                {
                    Task.Factory.StartNew(() => HandleContext(context));
                }
            }
        }

        public static async Task HandleContext(HttpListenerContext context)
        {
            HttpListenerRequest req = context.Request;
            HttpListenerResponse res = context.Response;

            res.SendChunked = false;

            res.Headers.Set(HttpResponseHeader.Server, "DSFILES");

            HttpMethod method = new HttpMethod(req.HttpMethod);

            Console.WriteLine($"[{DateTime.Now}] {method} {req.Url.PathAndQuery}");

            try
            {
                switch (req.Url.LocalPath.ToLowerInvariant().Split('/')[1])
                {
                    case "df" or "d" or "f":
                        if (method == HttpMethod.Get)
                        {
                            await DSFilesDownloadHandle.HandleFile(req, res);
                        }
                        else if (method == HttpMethod.Post)
                        {
                            await DSFilesUploadHandle.HandleFile(req, res);
                        }
                        break;

                    case "rd" or "r":
                        await RedirectHandler.HandleRedirect(req, res);
                        break;

                    case "generate_204":
                        res.StatusCode = 204;
                        break;

                    case "rick":
                        res.Redirect("https://youtu.be/dQw4w9WgXcQ");
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

                    case "cert" or "certs":
                        CertificatesHandler.HandleCertificate(req, res);
                        break;

                    case "favicon.ico":
                        res.SendStatus(404);
                        return;

                    default:
                        res.SendCatError(419);
                        //res.Send("Te perdiste o que señor patata");
                        break;
                }

                res.OutputStream.Close();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                res.Send(ex.ToString());
            }
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