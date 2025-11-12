using Microsoft.AspNetCore.Http;
using System.Collections.Specialized;
using System.Text;

namespace DSFiles_Server.Helpers
{
    public static class NameValueCollectionExtensions
    {
        public static string Get(this NameValueCollection collection, string key)
        {
            var values = collection.GetValues(key);

            return values.FirstOrDefault();
        }
    }

    public static class StreamExtensions
    {
        public static void Write(this Stream s, ref StringBuilder sb)
        {
            var data = Encoding.UTF8.GetBytes(sb.ToString());
            s.Write(data, 0, data.Length);
        }

        public static void Write(this Stream s, string d)
        {
            var data = Encoding.UTF8.GetBytes(d);
            s.Write(data, 0, data.Length);
        }

        public static async Task CopyToAsync(this Stream source, Stream destination, int offset, int bufferSize = 81920)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));

            int i = 0;
            bool fw = true;

            byte[] buffer = new byte[Math.Max(offset, bufferSize)];
            int bytesRead;

            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (fw)
                {
                    await destination.WriteAsync(buffer, offset, bytesRead - offset);

                    fw = false;
                }
                else
                {
                    await destination.WriteAsync(buffer, 0, bytesRead);
                }

                i++;
            }
        }
    }

    public static class HttpResponseExtensions
    {
        public static async Task SendStatus(this HttpResponse res, int status, string? data = null)
        {
            res.StatusCode = status;

            if (data == null)
            {
                return;
            }

            res.Headers["warning"] = (Convert.ToBase64String(Encoding.UTF8.GetBytes(data)));
            await res.WriteAsync(data);
        }

        public static async Task SendCatError(this HttpResponse res, int status)
        {
            res.ContentType = "text/html; charset=utf-8";
            await res.WriteAsync($"<!doctypehtml><html lang=en><meta charset=UTF-8><meta content=\"width=device-width,initial-scale=1\"name=viewport><style>body,html{{background-color:#000;margin:0;padding:0;height:100%;display:flex;justify-content:center;align-items:center;overflow:hidden}}img{{width:100%;height:100%;object-fit:cover}}</style><img alt=Image src=https://http.cat/{status}>");
        }

        public static void RedirectCatError(this HttpResponse res, int status)
        {
            res.Redirect("https://http.cat/" + status);
        }
    }
}