using DSFiles_Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DSFiles_Server.Helpers
{
    internal static class FileServerKestrel
    {
        public static void Configure(KestrelServerOptions options)
        {
            options.AddServerHeader = false;
            options.AllowSynchronousIO = false;
            options.Limits.MaxRequestBufferSize = 1024 * 1024;
            options.Limits.MaxResponseBufferSize = 64 * 1024;
            options.Limits.MaxRequestBodySize = 100_000_000;
            options.Limits.MaxRequestLineSize = ushort.MaxValue;
            options.Limits.MaxRequestHeadersTotalSize = ushort.MaxValue;
        }

        public static Task ApplyRequestLimits(HttpContext context, RequestDelegate next)
        {
            string path = (context.Request.Path.Value ?? "").TrimEnd('/');
            if (path.Equals("/animate", StringComparison.OrdinalIgnoreCase))
            {
                var bodyControl = context.Features.Get<IHttpBodyControlFeature>();
                if (bodyControl != null)
                {
                    bodyControl.AllowSynchronousIO = true;
                }
            }

            if (HttpMethods.IsPost(context.Request.Method))
            {
                long? limit = path.ToLowerInvariant() switch
                {
                    "/cuc" => DiscordFilesSpliter.CHUNK_SIZE,
                    "/cuf" or "/cua" => 8 * 1024 * 1024,
                    "/d" => 100_000_000,
                    "/shorten" => 8192,
                    "/cuh" or "/upload-challenge" or "/delete-challenge" or "/remove" => 0,
                    _ => null
                };
                if (limit.HasValue)
                {
                    if (context.Request.ContentLength > limit.Value)
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return Task.CompletedTask;
                    }

                    var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                    if (bodySize is { IsReadOnly: false })
                    {
                        bodySize.MaxRequestBodySize = limit.Value;
                    }
                }
            }

            return next(context);
        }
    }
}
