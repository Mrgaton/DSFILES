using System.Diagnostics;
using System.Net;

namespace DSFiles_Server
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            bool debug = Debugger.IsAttached;

            if (!debug)
            {
                SentrySdk.Init(o =>
                {
                    //o.AddAspNet();
                    o.Dsn = "https://5a58b08aca581b0c7d6b0ef79aedd518@o4507580376219648.ingest.us.sentry.io/4507580379889664";
                    o.Debug = false;
                    o.TracesSampleRate = 1.0;
                    o.AddEntityFramework();
                });
            }

            HttpListener listener = new HttpListener();

            listener.Prefixes.Add("http://*:8080/");
            listener.Start();

            Console.WriteLine("Listening on 8080...");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();

                Task.Factory.StartNew(() => HandleContext(context));
            }
        }

        public static async Task HandleContext(HttpListenerContext context)
        {
            HttpListenerRequest req = context.Request;
            HttpListenerResponse res = context.Response;

            res.SendChunked = false;

            Console.WriteLine($"[{DateTime.Now}] {req.Url.PathAndQuery}");

            try
            {
                switch (req.Url.LocalPath.ToLowerInvariant().Split('/')[1])
                {
                    case "f":
                    case "d":
                    case "df":
                        await DSFilesHandle.HandleFile(req, res);
                        break;

                    case "r":
                    case "rd":
                        await RedirectHandler.HandleRedirect(req, res);
                        break;

                    case "rick":
                        res.Redirect("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
                        res.Close();
                        break;

                    case "animate":
                        ConsoleAnimation.HandleAnimation(req, res);
                        break;

                    default:
                        res.RedirectCatError(404);
                        //res.Send("Te perdiste o que señor patata");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine('\n' + ex.ToString());
            }

            try
            {
                res.OutputStream.Close();
            }
            catch { }
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