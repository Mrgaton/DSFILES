using System.Diagnostics;
using System.Net;

namespace DSFiles_Server
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            SentrySdk.Init(o =>
            {
                //o.AddAspNet();
                o.Dsn = "https://5a58b08aca581b0c7d6b0ef79aedd518@o4507580376219648.ingest.us.sentry.io/4507580379889664";
                o.Debug = Debugger.IsAttached;
                o.TracesSampleRate = 1.0;
                o.AddEntityFramework();
            });

            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://*:8080/");
            listener.Start();

            Console.WriteLine("Listening...");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();

                Task.Factory.StartNew(() => HandleContext(ref context));
            }
        }

        public static void HandleContext(ref HttpListenerContext context)
        {
            HttpListenerRequest req = context.Request;
            HttpListenerResponse res = context.Response;

            res.SendChunked = false;

            Console.WriteLine($"[{DateTime.Now}] {req.Url.PathAndQuery}");

            switch (req.Url.LocalPath.ToLowerInvariant().Split('/')[1])
            {
                case "df":
                    DSFilesHandle.HandleFile(ref req, ref res);
                    break;

                case "rick":
                    res.Redirect("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
                    break;

                default:
                    res.Send("Te perdiste o que señor patata");
                    break;
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