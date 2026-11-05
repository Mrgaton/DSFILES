using DSFiles_Server.Helpers;
using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace DSFiles_Server.Routes
{
    internal static class RedirectHandler
    {
        public static Task HandleRedirect(HttpRequest req, HttpResponse res)
        {
            string seed = req.RouteValues["seed"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(seed))
            {
                res.StatusCode = 400;
                return Task.CompletedTask;
            }

            res.Redirect("/d/" + seed + "?redirect");
            return Task.CompletedTask;
        }

        public static async Task HandleFileRedirect(string[] attachments, long length, byte[]? key, bool compressed, HttpResponse res, CancellationToken ct)
        {
            res.Headers.CacheControl = "no-store";
            if (compressed || attachments.Length != 1 || length is < 1 or > 8192)
            {
                res.StatusCode = 422;
                await res.WriteAsync("The redirect must be an uncompressed UTF-8 URL of up to 8 KiB.", ct);
                return;
            }

            try
            {
                var urls = await DSFilesHelper.RefreshUrls(attachments);
                using var request = new HttpRequestMessage(HttpMethod.Get, urls.Single());
                using var response = await Program.client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                byte[] data = new byte[(int)length + 1];
                int count = 0;
                while (count < data.Length)
                {
                    int read = await body.ReadAsync(data.AsMemory(count), ct);
                    if (read == 0)
                    {
                        break;
                    }

                    count += read;
                }

                if (count != length)
                {
                    throw new InvalidDataException();
                }

                using var aes = new AesCTRStream(null, key);
                aes.Transform(data.AsSpan(0, count), 0);
                string target = new UTF8Encoding(false, true).GetString(data, 0, count);
                if (target.Any(char.IsControl) || !Uri.TryCreate(target, UriKind.Absolute, out var url) || (url.Scheme != "http" && url.Scheme != "https") || !string.IsNullOrEmpty(url.UserInfo))
                {
                    res.StatusCode = 422;
                    await res.WriteAsync("The file does not contain a valid HTTP or HTTPS URL.", ct);
                    return;
                }

                res.Headers["Referrer-Policy"] = "no-referrer";
                res.Redirect(url.AbsoluteUri);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or DecoderFallbackException)
            {
                res.StatusCode = 502;
                await res.WriteAsync("Could not read the redirect file.", ct);
            }
        }
    }
}
