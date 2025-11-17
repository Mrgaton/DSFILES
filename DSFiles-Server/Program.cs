using DSFiles_Server.Helpers;
using DSFiles_Server.Routes;
using DSFiles_Shared;
using DynamicCertificatePinning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DSFiles_Server
{
    internal static class Program
    {
#if DEBUG
        private const int PortNumber = 8081;
#else
        private const int PortNumber = 8080;
#endif

        public static HttpClient client = new HttpClient(new HttpClientHandler()
        {
            CookieContainer = new CookieContainer(80),
            AllowAutoRedirect = false,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls13 | System.Security.Authentication.SslProtocols.Tls12,
            MaxConnectionsPerServer = short.MaxValue,
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        private static async Task Main(string[] args)
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

                    string key = line.Substring(0, separatorIndex).ToUppeProcess.GetCurrentProcess().MainModule.FileNamer();

                    string value = line.Substring(separatorIndex + 1);

                    Environment.SetEnvironmentVariable(key, value);
                }
            }

            var envPass = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(Process.GetCurrentProcess().MainModule.FileName)));

            CertManager manager = new(new() { CertPassword = envPass });

            var cert = manager.GetOrCreateCert("qsap.gato.ovh");

            SslApplicationProtocol AppProtocol = new("quic-proto/1");
            var listenEndPoint = new IPEndPoint(IPAddress.Any, 443);

            var listenerOptions = new QuicListenerOptions
            {
                ListenEndPoint = listenEndPoint,
                ListenBacklog = 1024, // high backlog for bursts
                ApplicationProtocols = new List<SslApplicationProtocol> { AppProtocol },
                // Select connection options for each incoming connection
                ConnectionOptionsCallback = (connection, hello, token) =>
                {
                    var serverConnOpts = new QuicServerConnectionOptions
                    {
                        DefaultStreamErrorCode = 0x0A,
                        DefaultCloseErrorCode = 0x0B,

                        MaxInboundBidirectionalStreams = 20,
                        MaxInboundUnidirectionalStreams = 5,
                        KeepAliveInterval = TimeSpan.FromSeconds(15),

                        IdleTimeout = TimeSpan.FromMinutes(20),

                        ServerAuthenticationOptions = new SslServerAuthenticationOptions
                        {
                            ServerCertificate = cert,
                            ApplicationProtocols = new List<SslApplicationProtocol> { AppProtocol },
                            ClientCertificateRequired = false,
                            RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true // for testing only; do real validation in production
                        }
                    };


                    return ValueTask.FromResult(serverConnOpts);
                }
            };

           /* await using var listener = await QuicListener.ListenAsync(listenerOptions, CancellationToken.None);
            Console.WriteLine($"Listening QUIC on {listenEndPoint}...");
            
            _ = Task.Run(() => QuicServer.AcceptLoopAsync(listener, default));*/




         

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

            var builder = WebApplication.CreateBuilder(args);

            builder.WebHost.ConfigureKestrel((context, options) =>
            {
                options.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
                options.Limits.MaxRequestLineSize = ushort.MaxValue;
                options.Limits.MaxRequestHeadersTotalSize = ushort.MaxValue;


                options.ListenAnyIP(PortNumber, listenOptions =>
                {
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificateSelector = (ctx, host) =>
                        {
                            return manager.GetOrCreateCert(host);
                        };

                        httpsOptions.SslProtocols = System.Security.Authentication.SslProtocols.Tls13;
                    });

                    listenOptions.Protocols = HttpProtocols.Http2 | HttpProtocols.Http3;
                });
            });

            var app = builder.Build();

            app.UseWebSockets();

            //HttpListener listener = new HttpListener() { IgnoreWriteExceptions = false };
            //listener.Prefixes.Add($"http://*:{PortNumber}/");
            //listener.Prefixes.Add("http://localhost:9006/");
            //listener.Start();

            Console.WriteLine($"DSFILES awesome server listening on {PortNumber}...");

            app.MapGet("/", async (ctx) => await ctx.Response.SendCatError(419));

            app.MapGet("/generate_204", async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                await ctx.Response.WriteAsync("OK");
            });

            app.Use(async (ctx, next) =>
            {
                if (ctx.WebSockets.IsWebSocketRequest)
                {
                    var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false, DisableServerContextTakeover = true });

                    await WebSocketHandler.HandleWebSocket(ctx, ws);
                }
                else
                {
                    await next();
                }
            });


            //Redirector endpoints
            app.Map("/df/{**seed}", async (HttpContext ctx, string seed) =>
            {
                ctx.Response.Redirect("/d/" + seed);
            });

            app.Map("/f/{**seed}", async (HttpContext ctx, string seed) =>
            {
                ctx.Response.Redirect("/d/" + seed);
            });

            //Main endpoints
            app.MapGet("/d/{**seed}", async (HttpContext ctx, string seed) =>
            {
                await DSFilesDownloadHandle.HandleFile(ctx.Request, ctx.Response);
            });

            app.MapPost("/d", async (HttpContext ctx) =>
            {
                await DSFilesUploadHandle.HandleFile(ctx.Request, ctx.Response);
            });

            app.MapDelete("/d/{**seed}", async (HttpContext ctx, string seed) =>
            {
                await DSFilesRemoveHandle.HandleFile(ctx.Request, ctx.Response);
            });


            //Chunks upload system
            app.MapPost("/cuh", async (HttpContext ctx) =>
            {
                await DSFilesChunkedUploadHandle.HandleHandshake(ctx.Request, ctx.Response);
            });

            app.MapPost("/cuc", async (HttpContext ctx) =>
            {
                await DSFilesChunkedUploadHandle.HandleChunk(ctx.Request, ctx.Response);
            });

            //Weird jspaste based shortner
            app.MapGet("/rd", async (HttpContext ctx) =>
            {
                await RedirectHandler.HandleRedirect(ctx.Request, ctx.Response);
            });

            //Never gona give you up
            app.MapGet("/rick", async (HttpContext ctx) =>
            {
                ctx.Response.Redirect("https://youtu.be/dQw4w9WgXcQ");
            });

            //Pato.exe my beloved
            app.MapGet("/download", async (HttpContext ctx) =>
            {
                await SpeedTest.HandleDownload(ctx.Request, ctx.Response);
            });

            //IDk what is this shit
            app.MapGet("/upload", async (HttpContext ctx) =>
            {
                await SpeedTest.HandleUpload(ctx.Request, ctx.Response);
            });

            //Amazing console animation
            app.MapGet("/animate", async (HttpContext ctx) =>
            {
                await ConsoleAnimation.HandleAnimation(ctx.Request, ctx.Response);
            });

            app.MapGet("/favicon.ico", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                await ctx.Response.BodyWriter.WriteAsync(Convert.FromBase64String("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"));
                //return Task.CompletedTask;
            });

            app.Run();

            /*Task.Factory.StartNew(() =>
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
            }*/
        }

        /*
        public static async Task HandleContext(HttpListenerContext context)
        {
            HttpRequest req = context.Request;
            HttpResponse res = context.Response;

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
                        else if (method == HttpMethod.Delete)
                        {
                            await DSFilesRemoveHandle.HandleFile(req, res);
                        }
                        break;

                    case "cuh":
                        if (method == HttpMethod.Post)
                        {
                            await DSFilesChunkedUploadHandle.HandleHandshake(req, res);
                        }
                        break;

                    case "cuc":
                        if (method == HttpMethod.Post)
                        {
                            await DSFilesChunkedUploadHandle.HandleChunk(req, res);
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
                    case "cdn":
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
        }*/

        public static void WriteException(ref Exception ex, params string[] messages)
        {
            var lastColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(string.Join('\n', messages) + '\n' + ex.ToString() + '\n');
            Console.ForegroundColor = lastColor;
            Thread.Sleep(2000);
        }
    }
}