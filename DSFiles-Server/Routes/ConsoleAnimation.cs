using DSFiles_Server.Helpers;
using SkiaSharp;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace DSFiles_Server.Routes
{
    internal class ConsoleAnimation
    {
        private static int maxBrightness = 256 * 3;
        private static int colorLessMinCharSetLengh = 1;

        private static char[] charSet = " .,:;i1tfLCOG08@#".ToCharArray();

        private static int blankBrightNess = maxBrightness / charSet.Length * colorLessMinCharSetLengh;

        private sealed class ConversorConfig
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public bool Invert { get; set; }
            public bool HDR { get; set; }
            public bool Pixel { get; set; }
            public int FpsDivisor { get; set; }
            public int WidthDivisor { get; set; }
            public int HeightDivisor { get; set; }
            public int MinColorChangeNeeded { get; set; }
            public int BytesPerPixel { get; set; }
            public int RowBytes { get; set; }

            public ConversorConfig()
            { }
        }

        public static void HandleAnimation(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (req.Headers.Get("accept").Contains("html", StringComparison.InvariantCultureIgnoreCase))
            {
                res.RedirectCatError(418);
                return;
            }

            res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
            res.ContentType = "text/plain";

            //res.ContentLength64 = long.MaxValue;

            res.SendChunked = true;

            string gifName = "Gif";

            string url = req.QueryString["link"] ?? req.QueryString["l"] ?? req.QueryString["u"] ?? req.QueryString["url"];

            if (url == null)
            {
                res.Send("Please set the a photo or gif in the query parameter 'url'");
                return;
            }

            if (url.Contains("://tenor.com", StringComparison.InvariantCultureIgnoreCase))
            {
                url = TenorResolver.Resovlve(url);
            }

            var gifNameB = Encoding.UTF8.GetBytes(url.Split('/').Last().Split('.')[0].Replace('-', ' '));
            gifNameB[0] = (byte)char.ToUpper((char)gifNameB[0]);
            gifName = Encoding.UTF8.GetString(gifNameB);

            int timeOffset = int.Parse(req.QueryString["offset"] ?? req.QueryString["of"] ?? req.QueryString["o"] ?? "5");

            Dictionary<int, string> framesBuffer = new Dictionary<int, string>();

            int[] frameDuration = [];

            using (var ms = new MemoryStream(Program.client.GetByteArrayAsync(url).Result))
            using (var codec = SKCodec.Create(ms))
            {
                var info = codec.Info;

                var config = new ConversorConfig()
                {
                    Invert = bool.TryParse(req.QueryString["i"], out var invert) && invert,
                    HDR = bool.TryParse(req.QueryString["hdr"], out var hdr) && hdr,
                    Pixel = bool.TryParse(req.QueryString["pixel"] ?? req.QueryString["p"], out var pixel) && pixel,

                    Width = info.Width,
                    Height = info.Height,

                    FpsDivisor = Math.Max(1, int.Parse(req.QueryString["fps"] ?? req.QueryString["fd"] ?? req.QueryString["f"] ?? "1")),
                    WidthDivisor = Math.Max(1, int.Parse(req.QueryString["wd"] ?? req.QueryString["w"] ?? "1")),
                    HeightDivisor = Math.Max(1, int.Parse(req.QueryString["hd"] ?? req.QueryString["h"] ?? "1") * 2),
                    MinColorChangeNeeded = Math.Max(1, int.Parse(req.QueryString["mccn"] ?? req.QueryString["compression"] ?? req.QueryString["c"] ?? req.QueryString["comp"] ?? "75"))
                };

                StringBuilder sb = new StringBuilder();

                int transformedWidth = info.Width / config.WidthDivisor, transformedHeight = info.Height / config.HeightDivisor;

                sb.Append(ANSIHelper.ClearScreen);
                sb.Append(ANSIHelper.SetTitle("Wait while we compile the gif"));
                sb.Append(ANSIHelper.SetSize(transformedHeight + 2, transformedWidth));
                sb.Append(ANSIHelper.HideCursor);

                sb.Append(ANSIHelper.SetTitle(gifName + ' ' + transformedWidth + 'x' + (info.Height / (config.HeightDivisor / 2))));

                res.OutputStream.Write(ref sb);

                if (codec.FrameCount > 0)
                {
                    frameDuration = new int[codec.FrameCount];

                    int frames = codec.FrameCount / config.FpsDivisor;

                    for (int i = 0; i < frames; i++)
                    {
                        res.OutputStream.Write(ANSIHelper.SetPosition(0, 0) + "Please wait while we compile the gif " + i + '/' + frames + ' ' + framesBuffer.Sum(c => c.Value.Length) + " chars");

                        sb.Clear();
                        sb.Append(ANSIHelper.SetPosition(0, 0));

                        var bitmap = new SKBitmap(info.Width, info.Height);
                        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(i * config.FpsDivisor));

                        config.RowBytes = bitmap.RowBytes;
                        config.BytesPerPixel = bitmap.BytesPerPixel;

                        if (result == SKCodecResult.Success)
                        {
                            byte[] rgbValues = bitmap.Bytes;

                            RenderFrame(ref rgbValues, ref sb, config);
                        }

                        //Console.WriteLine(sb.ToString());

                        int acumulatedDelay = 0;

                        for (int y = i * config.FpsDivisor; i * config.FpsDivisor - config.FpsDivisor < y; y--)
                        {
                            if (i == 0)
                            {
                                acumulatedDelay = codec.FrameInfo[0].Duration;
                                break;
                            }

                            var frameInfo = codec.FrameInfo[y];
                            acumulatedDelay += frameInfo.Duration;
                        }

                        sb.Append(ANSIHelper.SetTitle(gifName + " F:" + i + '/' + frames + " C:" + sb.Length + " T:" + acumulatedDelay));

                        frameDuration[i] = acumulatedDelay;
                        framesBuffer.Add(i, sb.ToString());
                    }
                }
                else
                {
                    using (var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height)))
                    {
                        config.RowBytes = bitmap.RowBytes;
                        config.BytesPerPixel = bitmap.BytesPerPixel;

                        codec.GetPixels(bitmap.Info, bitmap.GetPixels());

                        var bytes = bitmap.Bytes;

                        RenderFrame(ref bytes, ref sb, config);
                        framesBuffer.Add(0, sb.ToString());
                    }
                }
            }

            Stopwatch sw = new Stopwatch();

            sw.Start();

            while (true)
            {
                for (int i = 0; i < framesBuffer.Count; i++)
                {
                    var data = framesBuffer[i];

                    res.OutputStream.Write(data);

                    if (framesBuffer.Count == 1)
                    {
                        while (true)
                        {
                            res.OutputStream.Write(ANSIHelper.SetPosition(0, 0));

                            Thread.Sleep(5 * 1000);
                        }
                    }

                    int timeToSleep = (int)(frameDuration[i] - sw.ElapsedMilliseconds) - timeOffset;

                    if (timeToSleep > 0)
                    {
                        Thread.Sleep(Math.Abs(timeToSleep));
                        Console.WriteLine("Sleeping " + timeToSleep);
                    }

                    sw.Restart();
                }
            }
        }

        private static void RenderFrame(ref byte[] buffer, ref StringBuilder sb, ConversorConfig config)
        {
            byte lastR = 0, lastG = 0, lastB = 0, lastA = 0;

            for (int y = 0; y < config.Height; y += config.HeightDivisor)
            {
                if (y > config.Height) break;

                for (int x = 0; x < config.Width; x += config.WidthDivisor)
                {
                    if (x > config.Width) break;

                    int index = y * config.RowBytes + x * config.BytesPerPixel;

                    byte r, g, b, a;

                    if (config.Invert)
                    {
                        r = buffer[index];
                        g = buffer[index + 1];
                        b = buffer[index + 2];
                        a = buffer[index + 3];
                    }
                    else
                    {
                        a = buffer[index + 3];
                        r = buffer[index + 2];
                        g = buffer[index + 1];
                        b = buffer[index];
                    }

                    int brightness = r + g + b;

                    char predictedChar = config.Pixel ? ' ' : charSet[brightness * charSet.Length / maxBrightness];

                    //MaximizeBrightness(ref pixelValue.Item2, ref pixelValue.Item1, ref pixelValue.Item0);
                    //QuantinizePixel(ref pixelValue.Item2, ref pixelValue.Item1, ref pixelValue.Item0);

                    int colorDiff = Math.Abs(r - lastR) + Math.Abs(g - lastG) + Math.Abs(b - lastB);

                    if ((config.Pixel || brightness > blankBrightNess) && colorDiff > config.MinColorChangeNeeded || ((r + g + b == 0) && lastR + lastG + lastB > 0)) //Dont change the color if its too similar to the current one
                    {
                        if (config.HDR)
                        {
                            MaximizeBrightness(ref r, ref g, ref b);
                        }

                        if (config.Pixel)
                        {
                            if (a < 255)
                            {
                                int alphaFactor = a + 1;

                                r = (byte)((r * alphaFactor) >> 8);
                                g = (byte)((g * alphaFactor) >> 8);
                                b = (byte)((b * alphaFactor) >> 8);
                            }

                            sb.Append(ANSIHelper.BRGB(predictedChar, r, g, b));
                        }
                        else
                        {
                            sb.Append(ANSIHelper.FRGB(predictedChar, r, g, b));
                        }

                        lastR = r;
                        lastG = g;
                        lastB = b;
                        lastA = a;
                    }
                    else
                    {
                        sb.Append(predictedChar);
                    }
                }

                sb.Append('\n');
            }
        }

        private const byte quantinizeValue = 16;

        public static void QuantinizePixel(ref byte R, ref byte G, ref byte B)
        {
            R = (byte)(R / quantinizeValue * quantinizeValue);
            G = (byte)(G / quantinizeValue * quantinizeValue);
            B = (byte)(B / quantinizeValue * quantinizeValue);
        }

        public static void MaximizeBrightness(ref byte R, ref byte G, ref byte B)
        {
            byte maxOriginal = Math.Max(R, Math.Max(G, B));

            if (maxOriginal > 0)
            {
                double factor = 255.0 / maxOriginal;

                R = (byte)(R * factor);
                G = (byte)(G * factor);
                B = (byte)(B * factor);
            }
        }

        public class ANSIHelper
        {
            public static string SetSize(int x, int y) => "\u001b[8;" + x + ";" + y + "t";

            public static string HideScrollbar => "\u001b[?30l";
            public static string ShowScrollbar => "\u001b[?30h";

            public static string HideCursor => "\u001b[?25l";
            public static string ShowCursor => "\u001b[?25h";

            public static string ClearScreen => "\u001b[2J";
            public static string ClearCursorToEnd => "\u001b[0J";
            public static string ClearCursorToBeginning => "\u001b[1J";
            public static string ClearLineToEnd => "\u001b[0K";
            public static string ClearStartToLine => "\u001b[1K";
            public static string ClearLine => "\u001b[2K";

            public static string MoveCursorUp(int n) => $"\u001b[{n}A";

            public static string MoveCursorDown(int n) => $"\u001b[{n}B";

            public static string MoveCursorForward(int n) => $"\u001b[{n}C";

            public static string MoveCursorBackward(int n) => $"\u001b[{n}D";

            public static string MoveCursorToPosition(int row, int col) => $"\u001b[{row};{col}H";

            public static string SaveCursorPosition => "\u001b[s";
            public static string RestoreCursorPosition => "\u001b[u";

            public static string ScrollUp(int n) => $"\u001b[{n}S";

            public static string ScrollDown(int n) => $"\u001b[{n}T";

            public static string SetTitle(string title) => "\u001b]0;" + title + "\a";

            public static string SetWindowSize(int x, int y) => "\u001b[8;" + y + ";" + x + "t";

            public static string ResetColor => "\u001b[0m";

            //private static string Pastel(string text, int r, int g, int b) => "\u001b[38;2;" + r + ";" + g + ";" + b + "m" + text;
            public static string FRGB(char c, int r, int g, int b) => "\u001b[38;2;" + r + ";" + g + ";" + b + "m" + c;

            public static string BRGB(char c, int r, int g, int b) => "\u001b[48;2;" + r + ";" + g + ";" + b + "m" + c;

            public static string SetPosition(int row, int collum) => "\u001b[" + row + ";" + collum + "H";
        }
    }
}