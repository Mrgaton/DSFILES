using DSFiles_Server.Helpers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DSFiles_Server.Routes
{
    internal static class CertificatesHandler
    {
        public static ConcurrentDictionary<string, (DateTime notAfter, string json)> CertsCache = new();

        public static X509Certificate2 GetCertificate(string domain, int port = 443, int retries = 4, int delayMilliseconds = 1250)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new ArgumentException("Domain must not be null or empty.", nameof(domain));
            }

            int attempt = 0;

            while (attempt < retries)
            {
                try
                {
                    using (var client = new TcpClient(domain, port))
                    using (var sslStream = new SslStream(client.GetStream(), false, (sender, certificate, chain, sslPolicyErrors) => true))
                    {
                        sslStream.AuthenticateAsClient(domain);

                        X509Certificate remoteCertificate = sslStream.RemoteCertificate;

                        if (remoteCertificate == null)
                        {
                            throw new Exception("Failed to retrieve the certificate from the server.");
                        }

                        return new X509Certificate2(remoteCertificate);
                    }
                }
                catch (Exception ex)
                {
                    attempt++;

                    Console.WriteLine($"Attempt {attempt} failed: {ex.Message}");

                    if (attempt >= retries)
                    {
                        Console.WriteLine("Max retry attempts reached. Unable to fetch the certificate.");
                        return null;
                    }

                    Thread.Sleep(delayMilliseconds);
                }
            }

            return null;
        }
        public static string GetCertCacheAge(DateTime notAfter)
        {
            return $"public, max-age={(long)Math.Max(0, (notAfter.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)}";
        }
        public static async Task HandleCertificate(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string[] urlSplited = req.Url.AbsolutePath.Split('/');
                string domain = urlSplited.Last();

                res.Headers.Set("content-type", "application/json");

                if (CertsCache.TryGetValue(domain, out var entry))
                {
                    res.AddHeader("Cache-Control", GetCertCacheAge(entry.notAfter));
                    res.Send(entry.json);
                    return;
                }

                X509Certificate2 cert2 = GetCertificate(domain);

                var obj = new
                {
                    PublicKey = cert2.PublicKey.EncodedKeyValue.RawData,
                    Subject = cert2.Subject,
                    Issuer = cert2.Issuer,
                    Expiration = cert2.NotAfter
                };

                var json = JsonSerializer.Serialize(obj);

                CertsCache.TryAdd(domain, (cert2.NotAfter, json));
                res.AddHeader("Cache-Control", GetCertCacheAge(cert2.NotAfter));
                res.Send(json);
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                res.Send(ex.ToString());

                Console.WriteLine(ex.ToString());
            }
        }
    }
}