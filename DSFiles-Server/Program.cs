using System.Net;

namespace DSFiles_Server
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://*:8080/");
            listener.Start();
            Console.WriteLine("Listening...");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();

                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                response.SendChunked = false;

                Console.WriteLine($"[{DateTime.Now}] {request.Url.PathAndQuery}");

                switch (request.Url.LocalPath.ToLowerInvariant().Split('/')[1])
                {
                    case "df":
                        DSFilesHandle.HandleFile(ref request, ref response);
                        break;

                    case "rick":
                        response.Redirect("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
                        break;

                    default:
                        response.Send("Te perdiste o que señor patata");
                        break;
                }

                if (response.OutputStream.CanWrite)
                {
                    try
                    {
                        //response.Close();
                    }
                    catch
                    {

                    }
                }
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