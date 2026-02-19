using DSFiles_Server.Helpers;
using Microsoft.AspNetCore.Http;
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

        public static async Task<X509Certificate2> GetCertificate(string domain, int port = 443, int retries = 4, int delayMilliseconds = 1250)
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
                    {
                        await client.ConnectAsync(domain, port);

                        using var sslStream = new SslStream(client.GetStream(), false, (sender, certificate, chain, sslPolicyErrors) => new X509Certificate2(certificate).Verify());

                        await sslStream.AuthenticateAsClientAsync(domain);

                        X509Certificate remoteCertificate = sslStream.RemoteCertificate;

                        if (remoteCertificate == null)
                        {
                            throw new NullReferenceException("Failed to retrieve the certificate from the server.");
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
                        throw;
                    }
                }
            }

            throw new Exception("Unable to fetch the certificate after multiple attempts.");
        }
        public static string GetCertCacheAge(DateTime notAfter)
        {
            return $"public, max-age={(long)Math.Max(0, (notAfter.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)}";
        }
        public static async Task HandleCertificate(HttpRequest req, HttpResponse res)
        {
            try
            {
                string[] urlSplited = req.Path.ToString().Split('/');
                string domain = urlSplited.LastOrDefault();

                if (domain == null || string.IsNullOrWhiteSpace(domain))
                {
                    res.StatusCode = 400;
                    await res.WriteAsync("Domain not specified.");
                    return;
                }

                domain = domain.ToLower();

                if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Contains("localhost"))
                {
                    res.StatusCode = 400;
                    await res.WriteAsync("Invalid domain or internal address not allowed.");
                    return;
                }

                res.Headers["content-type"] = "application/json";

                if (CertsCache.TryGetValue(domain, out var entry))
                {
                    res.Headers["Cache-Control"] = GetCertCacheAge(entry.notAfter);
                    await res.WriteAsync(entry.json);
                    return;
                }

                X509Certificate2 cert2 = await GetCertificate(domain);

                var obj = new
                {
                    PublicKey = cert2.PublicKey.EncodedKeyValue.RawData,
                    Subject = cert2.Subject,
                    Issuer = cert2.Issuer,
                    Expiration = cert2.NotAfter
                };

                var json = JsonSerializer.Serialize(obj);

                CertsCache.TryAdd(domain, (cert2.NotAfter, json));
                res.Headers["Cache-Control"] = ( GetCertCacheAge(cert2.NotAfter));
                await res.WriteAsync(json);
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                Console.Error.WriteLine(ex.ToString());
                throw;
            }
        }
    }
}