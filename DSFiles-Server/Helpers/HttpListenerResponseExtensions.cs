using System.Collections.Specialized;
using System.Net;
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
    }

    public static class HttpListenerResponseExtensions
    {
        public static void SendStatus(this HttpListenerResponse res, int status, string data = null)
        {
            res.StatusCode = status;

            if (data == null)
            {
                res.Close();
                return;
            }

            res.Send(Encoding.UTF8.GetBytes(data));
        }

        public static void RedirectCatError(this HttpListenerResponse res, int status)
        {
            res.Redirect("https://http.cat/" + status);
            res.Close();
        }

        public static void Send(this HttpListenerResponse res, string data) => res.Send(Encoding.UTF8.GetBytes(data));

        public static void Send(this HttpListenerResponse res, byte[] data)
        {
            try
            {
                res.ContentLength64 = data.Length;
                res.OutputStream.Write(data, 0, data.Length);
            }
            catch { }
        }
    }
}