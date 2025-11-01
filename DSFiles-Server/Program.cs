using DSFiles_Server.Helpers;
using DSFiles_Server.Routes;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DSFiles_Server
{
    internal static class Program
    {
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

        private static void Main(string[] args)
        {
            /*Stopwatch totalStopwatch = new Stopwatch();
            Stopwatch compressionStopwatch = new Stopwatch();
            Stopwatch decompressionStopwatch = new Stopwatch();

            totalStopwatch.Start();

            int dataSize = 1024 * 1024 * 64;
            int keySize = 32;

            byte[] keyd = RandomNumberGenerator.GetBytes(keySize);

            Console.WriteLine($"Generated a {keySize}-byte key.");

            byte[] originalData = (new byte[dataSize]).Select(e => e = 100).ToArray();
            
            Console.WriteLine($"Generated {dataSize / (1024 * 1024)} MB of random data.");

            byte[] firstPassData = null;
            byte[] secondPassData = null;
            bool verificationSucceeded = false;

            Console.WriteLine("\n--- Starting First Transformation Pass ---");
            try
            {
                using (MemoryStream originalMs = new MemoryStream(originalData))
                using (MemoryStream firstPassMs = new MemoryStream())
                using (AesCTRStream transform1 = new AesCTRStream(originalMs, keyd))
                {
                    transform1.CopyTo(firstPassMs);
                    firstPassData = firstPassMs.ToArray();
                    Console.WriteLine("First transformation completed.");
                    Console.WriteLine($"Processed {firstPassData.Length} bytes.");

                    if (dataSize > 0 && !originalData.SequenceEqual(firstPassData))
                    {
                        Console.WriteLine("Intermediate data is different from original (as expected).");
                    }
                    else if (dataSize > 0)
                    {
                        Console.WriteLine("WARNING: Intermediate data is THE SAME as original. The transform might be ineffective or identity.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during first transformation: {ex.Message}");
                totalStopwatch.Stop();
                return;
            }


            Console.WriteLine("\n--- Starting Second Transformation Pass ---");
            if (firstPassData == null)
            {
                Console.WriteLine("Error: First pass data is null, cannot proceed.");
                totalStopwatch.Stop();
                return;
            }
            try
            {
                using (MemoryStream firstPassInputMs = new MemoryStream(firstPassData))
                using (MemoryStream secondPassMs = new MemoryStream()) 
                                                                       
                using (AesCTRStream transform2 = new AesCTRStream(firstPassInputMs, keyd))
                {
                    transform2.CopyTo(secondPassMs);
                    secondPassData = secondPassMs.ToArray();
                    Console.WriteLine("Second transformation completed.");
                    Console.WriteLine($"Processed {secondPassData.Length} bytes.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during second transformation: {ex.Message}");
                totalStopwatch.Stop();
                return;
            }

            Console.WriteLine("\n--- Verification ---");
            if (originalData.Length != secondPassData?.Length)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: Length mismatch! Original: {originalData.Length}, Final: {secondPassData?.Length ?? -1}");
                Console.ResetColor();
            }
            else
            {
                bool areEqual = originalData.SequenceEqual(secondPassData);

                if (areEqual)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("SUCCESS: Original data and final data are identical.");
                    Console.WriteLine("The transformation IS reversible with the same parameters.");
                    Console.ResetColor();
                    verificationSucceeded = true;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAILURE: Original data and final data differ.");
                    Console.WriteLine("The transformation IS NOT reversible or there's a bug.");
                    Console.ResetColor();

                    for (int i = 0; i < Math.Min(originalData.Length, secondPassData.Length); ++i)
                    {
                        if (originalData[i] != secondPassData[i])
                        {
                            Console.WriteLine($"First difference found at index {i}: Original=0x{originalData[i]:X2}, Final=0x{secondPassData[i]:X2}");
                            break;
                        }
                    }
                }
            }

            totalStopwatch.Stop();

            if (verificationSucceeded)
            {
                Console.WriteLine("\n--- Compression of Transformed Data ---");
                byte[] compressedData = null;
                byte[] decompressedData = null;

                try
                {
                    compressionStopwatch.Start();
                    using (MemoryStream compressedMs = new MemoryStream())
                    {
                        using (BrotliStream gzipStream = new BrotliStream(compressedMs, CompressionMode.Compress, leaveOpen: true)) // leaveOpen allows compressedMs to be read later
                        {
                             gzipStream.Write(firstPassData);
                        }

                        compressedData = compressedMs.ToArray();
                    }
                    compressionStopwatch.Stop();
                    double compressionRatio = (double)compressedData.Length / firstPassData.Length;
                    Console.WriteLine($"Compressed size: {compressedData.Length} bytes");
                    Console.WriteLine($"Compression Ratio: {compressionRatio:P4}");
                    Console.WriteLine($"Time to compress: {compressionStopwatch.ElapsedMilliseconds} ms");

                    if (firstPassData.SequenceEqual(decompressedData))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("SUCCESS: Decompressed data matches the first pass transformed data.");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("FAILURE: Decompressed data does NOT match the first pass transformed data!");
                        Console.ResetColor();
                    }

                }
                catch (Exception ex)
                {
                    compressionStopwatch.Stop();
                    decompressionStopwatch.Stop();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error during compression/decompression: {ex.Message}");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.WriteLine("\n--- Compression Skipped (Verification Failed) ---");
            }

            return;*/

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

            /*var builder = WebApplication.CreateBuilder(args);

            builder.WebHost.ConfigureKestrel((context, options) =>
            {
                options.ListenAnyIP(8081, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;

                    //listenOptions.UseHttps();
                });
            });

            var app = builder.Build();

            app.MapGet("/", () => "Hello World!");

            Task.Factory.StartNew(() => app.Run());*/

            HttpListener listener = new HttpListener() { IgnoreWriteExceptions = false };

            listener.Prefixes.Add("http://*:8081/");
            //listener.Prefixes.Add("http://localhost:9006/");
            listener.Start();

            Console.WriteLine("DSFILES awesome server listening on 8080...");

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