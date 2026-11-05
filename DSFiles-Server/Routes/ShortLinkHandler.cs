using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DSFiles_Server.Routes
{
    internal static class ShortLinkHandler
    {
        private const string Endpoint = "https://jspaste.eu/api/v2/documents";
        private static readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        internal static bool IsValidKey(string key) => Regex.IsMatch(key, "^[A-Za-z0-9_-]{4,64}$");

        private static bool IsValidUrl(string text)
        {
            return !text.Any(char.IsControl)
                && Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && (uri.Scheme == "https" || uri.Scheme == "http")
                && string.IsNullOrEmpty(uri.UserInfo);
        }

        private static async Task<string> ReadDocument(HttpContent content, CancellationToken cancellationToken)
        {
            await content.LoadIntoBufferAsync(8192, cancellationToken);
            return await content.ReadAsStringAsync(cancellationToken);
        }

        public static async Task Create(HttpContext context)
        {
            var req = context.Request;
            var res = context.Response;
            var cancellationToken = context.RequestAborted;
            res.Headers.CacheControl = "no-store";
            if (req.ContentLength is not > 0 or > 8192)
            {
                res.StatusCode = 400;
                return;
            }

            long lifetime = 0;
            if (req.Headers.TryGetValue("lifetime", out var lifetimeValue)
                && (!long.TryParse(lifetimeValue, out lifetime)
                    || (lifetime != 0 && (lifetime < 60 || lifetime > 31536000))))
            {
                res.StatusCode = 400;
                await res.WriteAsync("Expiration must be 0 or between 60 and 31536000 seconds.", cancellationToken);
                return;
            }

            if (UploadProof.Consume(req, req.ContentLength.Value) != true)
            {
                res.StatusCode = 403;
                return;
            }

            using var reader = new StreamReader(req.Body, new UTF8Encoding(false, true));
            string url = await reader.ReadToEndAsync(cancellationToken);
            if (!IsValidUrl(url))
            {
                res.StatusCode = 400;
                await res.WriteAsync("Invalid HTTP or HTTPS URL.", cancellationToken);
                return;
            }

            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(url, Encoding.UTF8, "text/plain")
                };
                request.Headers.Add("keyLength", "6");
                request.Headers.Add("secret", secret);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await ReadDocument(response.Content, cancellationToken));
                string key = json.RootElement.GetProperty("key").GetString() ?? "";
                if (!IsValidKey(key))
                {
                    throw new InvalidDataException();
                }

                string removeToken = "jsp:" + key + ":" + secret;
                long expiresAt = lifetime > 0 ? DateTimeOffset.UtcNow.AddSeconds(lifetime).ToUnixTimeMilliseconds() : 0;
                if (expiresAt > 0)
                {
                    try
                    {
                        await context.RequestServices.GetRequiredService<ShortLinkExpiryQueue>().Add(key, removeToken, expiresAt);
                    }
                    catch
                    {
                        try
                        {
                            await DeleteDocument(removeToken, CancellationToken.None);
                        }
                        catch
                        {
                        }

                        res.StatusCode = 503;
                        await res.WriteAsJsonAsync(
                            new ShortLinkRecoveryResponse("Could not schedule deletion. Keep the token in case you need to delete the link manually.", key, removeToken),
                            AppJsonContext.Default.ShortLinkRecoveryResponse, cancellationToken: cancellationToken);
                        return;
                    }
                }

                await res.WriteAsJsonAsync(new ShortLinkResponse(key, removeToken, expiresAt), AppJsonContext.Default.ShortLinkResponse, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                res.StatusCode = 502;
                await res.WriteAsync("JSPaste did not confirm link creation. Try again later.", cancellationToken);
            }
        }

        public static async Task Redirect(HttpContext context)
        {
            var res = context.Response;
            var cancellationToken = context.RequestAborted;
            res.Headers.CacheControl = "no-store";
            res.Headers["Referrer-Policy"] = "no-referrer";
            string key = context.Request.RouteValues["key"]?.ToString() ?? "";
            if (!IsValidKey(key))
            {
                res.StatusCode = 400;
                return;
            }

            if (await context.RequestServices.GetRequiredService<ShortLinkExpiryQueue>().Expired(key))
            {
                res.StatusCode = 410;
                await res.WriteAsync("This link has expired. Its deletion from JSPaste is scheduled.", cancellationToken);
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/" + key + "/raw");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                {
                    res.StatusCode = 410;
                    await res.WriteAsync("The link is no longer available on JSPaste. Its data may have been deleted.", cancellationToken);
                    return;
                }

                response.EnsureSuccessStatusCode();
                string target = await ReadDocument(response.Content, cancellationToken);
                if (!IsValidUrl(target))
                {
                    throw new InvalidDataException();
                }

                res.Redirect(target);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                res.StatusCode = 502;
                await res.WriteAsync("Could not resolve the link on JSPaste. This external service may be unavailable.", cancellationToken);
            }
        }

        public static async Task Remove(string token, HttpResponse res, CancellationToken cancellationToken)
        {
            var parts = token.Split(':');
            if (parts.Length != 3 || !IsValidKey(parts[1]) || !Regex.IsMatch(parts[2], "^[a-f0-9]{48}$"))
            {
                res.StatusCode = 400;
                return;
            }

            try
            {
                await DeleteDocument(token, cancellationToken);
                await res.HttpContext.RequestServices.GetRequiredService<ShortLinkExpiryQueue>().Forget(parts[1]);
                await res.WriteAsJsonAsync(new RemovalResponse(1, 1), AppJsonContext.Default.RemovalResponse, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                res.StatusCode = 502;
                await res.WriteAsync("JSPaste did not confirm deletion. Keep the token and try again.", cancellationToken);
            }
        }

        internal static async Task DeleteDocument(string token, CancellationToken cancellationToken)
        {
            var parts = token.Split(':');
            using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint + "/" + parts[1]);
            request.Headers.Add("secret", parts[2]);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound && response.StatusCode != HttpStatusCode.Gone)
            {
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await ReadDocument(response.Content, cancellationToken));
                if (!json.RootElement.TryGetProperty("removed", out var removed) || removed.ValueKind != JsonValueKind.True)
                {
                    throw new InvalidDataException();
                }
            }
        }
    }
}
