using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text.Json.Nodes;

namespace DSFiles_Server.Routes
{
    internal static class DSFilesRemoveHandle
    {
        public static async Task HandleFile(HttpRequest req, HttpResponse res)
        {
            res.Headers.CacheControl = "no-store";
            string token = req.Headers["Remove-Token"].FirstOrDefault() ?? Uri.UnescapeDataString(req.Path.ToString().Split('/').Last());
            if (!UploadProof.ConsumeDelete(req, token))
            {
                res.StatusCode = StatusCodes.Status403Forbidden;
                await res.WriteAsJsonAsync(new ApiErrorResponse("A valid, unexpired deletion proof is required."), AppJsonContext.Default.ApiErrorResponse);
                return;
            }

            if (token.StartsWith("jsp:", StringComparison.Ordinal))
            {
                await ShortLinkHandler.Remove(token, res, req.HttpContext.RequestAborted);
                return;
            }

            if (token.StartsWith("srv:", StringComparison.Ordinal))
            {
                try
                {
                    token = Helpers.UploadReceipt.Unprotect(token);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    res.StatusCode = 400;
                    await res.WriteAsync("Invalid deletion token.", req.HttpContext.RequestAborted);
                    return;
                }
            }

            ulong webhookId;
            string webhookToken;
            ulong[] ids;
            try
            {
                var parts = token.Split(':');
                if (token.Length > 60000 || parts.Length != 3)
                {
                    throw new FormatException();
                }

                var idBytes = parts[1].FromBase64Url();
                if (idBytes.Length != 8 || parts[2].Length is < 1 or > 256 || !parts[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                {
                    throw new FormatException();
                }

                webhookId = BitConverter.ToUInt64(idBytes);
                webhookToken = parts[2];
                ids = new DiscordFilesSpliter.GorillaTimestampCompressor().Decompress(parts[0].FromBase64Url());
                if (webhookId == 0 || ids.Length is < 1 or > 10000 || ids.Any(id => id == 0))
                {
                    throw new FormatException();
                }

                ids = ids.Distinct().ToArray();
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                res.StatusCode = StatusCodes.Status400BadRequest;
                await res.WriteAsJsonAsync(new ApiErrorResponse("Invalid deletion token."), AppJsonContext.Default.ApiErrorResponse);
                return;
            }

            int removed = 0;
            try
            {
                foreach (ulong id in ids)
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://discord.com/api/webhooks/{webhookId}/{webhookToken}/messages/{id}");
                        using var response = await Program.client.SendAsync(request, req.HttpContext.RequestAborted);
                        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                        {
                            removed++;
                            break;
                        }

                        if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                        {
                            double seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1;
                            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(req.HttpContext.RequestAborted));
                            if (json?["retry_after"] != null)
                            {
                                seconds = (double)json["retry_after"]!;
                            }

                            if (!double.IsFinite(seconds))
                            {
                                throw new HttpRequestException("Invalid retry delay.");
                            }

                            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60)), req.HttpContext.RequestAborted);
                            continue;
                        }

                        throw new HttpRequestException("Discord did not confirm deletion.");
                    }
                }

                await res.WriteAsJsonAsync(new RemovalResponse(removed, ids.Length), AppJsonContext.Default.RemovalResponse, cancellationToken: req.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                if (req.HttpContext.RequestAborted.IsCancellationRequested)
                {
                    return;
                }

                res.StatusCode = StatusCodes.Status502BadGateway;
                await res.WriteAsJsonAsync(new PartialRemovalResponse("Discord did not confirm complete deletion. You can retry.", removed, ids.Length), AppJsonContext.Default.PartialRemovalResponse);
            }
        }
    }
}
