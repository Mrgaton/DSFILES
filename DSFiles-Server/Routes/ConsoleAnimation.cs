using DSFiles_Server.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using SkiaSharp;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DSFiles_Server.Routes
{
    internal static class ConsoleAnimation
    {
        private static int maxBrightness = 256 * 3;
        private static int colorLessMinCharSetLengh = 1;

        private static char[] charSet = " .,:;i1tfLCOG08@#".ToCharArray();

        private static int blankBrightNess = maxBrightness / charSet.Length * colorLessMinCharSetLengh;

        private sealed class ConversorConfig
        {
            public string Name { get; set; }
            public bool Invert { get; set; }
            public bool HDR { get; set; }
            public bool Pixel { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int FpsDivisor { get; set; }
            public int WidthDivisor { get; set; }
            public int HeightDivisor { get; set; }
            public int MinColorChangeNeeded { get; set; }
            public int BytesPerPixel { get; set; }
            public int RowBytes { get; set; }

            public ConversorConfig()
            { }
        }

        private static async Task HandleInformation(HttpRequest req, HttpResponse res)
        {
            await res.WriteAsync(@"Information of ConsoleAnimation endpoint query params:

link | url = the url of the media you want to convert to ascii

offset | o = the offset on wich the frames of the animated image is transmited in ms (example 30 or -60)

i = inverts the colors?

hdr = Some weird algorithm to maximize color

pixel = instead of characters just paints the whole char

fps = the amount of fps to divide

wd = (width divisor) the amount of pixels in the width row that skips to give a smaller charmap to the console

hd = (height divisor) the amount of pixels in the height row that skips to give a smaller charmap to the console

mccn | compression = the amount of rgb value that have to change to change the color for the next pixel for the console

");
        }

        public static async Task HandleAnimation(HttpRequest req, HttpResponse res)
        {
            if (req.Headers.TryGetValue("accept", out var accept) && ((string)accept).Contains("html", StringComparison.InvariantCultureIgnoreCase))
            {
                HandleInformation(req, res);
                return;
            }

            res.Headers["Cache-Control"] = ("no-cache, no-store, no-transform");
            res.ContentType = "text/plain";

            //res.ContentLength64 = long.MaxValue;


            string gifName = "Gif";

            string url = req.Query["link"].FirstOrDefault() ?? req.Query["l"].FirstOrDefault() ?? req.Query["u"].FirstOrDefault() ?? req.Query["url"];

            if (url == null)
            {
                await res.WriteAsync("Please set the a photo or gif in the query parameter 'url'");
                return;
            }

            if (url.Contains("://tenor.com", StringComparison.InvariantCultureIgnoreCase))
            {
                url = TenorResolver.Resovlve(url);
            }

            var gifNameByte = Encoding.UTF8.GetBytes(url.Split('/').Last().Split('.')[0].Replace('-', ' '));
            gifNameByte[0] = (byte)char.ToUpper((char)gifNameByte[0]);
            gifName = Encoding.UTF8.GetString(gifNameByte);

            int timeOffset = int.Parse(req.Query["offset"].FirstOrDefault() ?? req.Query["of"].FirstOrDefault() ?? req.Query["o"].FirstOrDefault() ?? "-8");

            var config = new ConversorConfig()
            {
                Name = gifName,

                Invert = bool.TryParse(req.Query["i"], out var invert) && invert,
                HDR = bool.TryParse(req.Query["hdr"], out var hdr) && hdr,
                Pixel = bool.TryParse(req.Query["pixel"].FirstOrDefault() ?? req.Query["p"], out var pixel) && pixel,

                FpsDivisor = Math.Max(1, int.Parse(req.Query["fps"].FirstOrDefault() ?? req.Query["fd"].FirstOrDefault() ?? req.Query["f"].FirstOrDefault() ?? "1")),
                WidthDivisor = Math.Max(1, int.Parse(req.Query["wd"].FirstOrDefault() ?? req.Query["w"].FirstOrDefault() ?? "1")),
                HeightDivisor = Math.Max(1, int.Parse(req.Query["hd"].FirstOrDefault() ?? req.Query["h"].FirstOrDefault() ?? "1") * 2),
                MinColorChangeNeeded = Math.Max(1, int.Parse(req.Query["mccn"].FirstOrDefault() ?? req.Query["compression"].FirstOrDefault() ?? req.Query["c"].FirstOrDefault() ?? req.Query["comp"].FirstOrDefault() ?? "75"))
            };

            Stopwatch sw = new Stopwatch();

            sw.Start();

            (int[] frameDuration, byte[][] rawFrames) = EncodeFrames(config, new MemoryStream(Program.client.GetByteArrayAsync(url).Result), res.Body);

            while (true)
            {
                for (int i = 0; i < rawFrames.Length; i++)
                {
                    var data = rawFrames[i];

					res.Body.Write(data, 0, data.Length);

                    if (rawFrames.Length == 1)
                    {
                        while (true)
                        {
							res.Body.Write(AnsiHelper.SetPosition(0, 0));

                            Thread.Sleep(5 * 1000);
                        }
                    }

                    int timeToSleep = (int)(frameDuration[i] - sw.ElapsedMilliseconds) + timeOffset;

                    if (timeToSleep > 0)
                    {
                        Thread.Sleep(Math.Abs(timeToSleep));
                        Console.WriteLine("Sleeping " + timeToSleep);
                    }

                    sw.Restart();
                }
            }
        }

        private static (int[] FramesDurration, byte[][] Frames) EncodeFrames(ConversorConfig config, MemoryStream stream, Stream compilationInfo)
        {
            using (var codec = SKCodec.Create(stream))
            {
                var info = codec.Info;

                config.Width = info.Width;
                config.Height = info.Height;

                int[] frameDuration = [];
                byte[][] rawFrames = new byte[1][];

                StringBuilder sb = new StringBuilder();

                if (codec.FrameCount > 0)
                {
                    int transformedWidth = info.Width / config.WidthDivisor, transformedHeight = info.Height / config.HeightDivisor;

                    sb.Append(AnsiHelper.ClearScreen);
                    sb.Append(AnsiHelper.SetTitle("Wait while we compile the gif"));
                    sb.Append(AnsiHelper.SetSize(transformedHeight + 2, transformedWidth + 1));
                    sb.Append(AnsiHelper.HideCursor);

                    sb.Append(AnsiHelper.SetTitle(config.Name + ' ' + transformedWidth + 'x' + (info.Height / (config.HeightDivisor / 2))));

                    compilationInfo.Write(ref sb);

                    frameDuration = new int[codec.FrameCount];

                    int frames = codec.FrameCount / config.FpsDivisor;

                    rawFrames = new byte[frames][];

                    for (int i = 0; i < frames; i++)
                    {
                        compilationInfo.Write(AnsiHelper.SetPosition(0, 0) + "Please wait while we compile the gif " + i + '/' + frames + ' ' + rawFrames.Sum(c => c != null ? c.Length : 0) + " chars");

                        sb.Clear();
                        sb.Append(AnsiHelper.SetPosition(0, 0));

                        var bitmap = new SKBitmap(info.Width, info.Height);
                        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(i * config.FpsDivisor));

                        config.RowBytes = bitmap.RowBytes;
                        config.BytesPerPixel = bitmap.BytesPerPixel;

                        if (result == SKCodecResult.Success)
                        {
                            byte[] rgbValues = bitmap.Bytes;

                            RenderFrame(ref rgbValues, ref sb, ref config);
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

                        sb.Append(AnsiHelper.SetTitle(config.Name + " F:" + i + '/' + frames + " C:" + sb.Length + " T:" + acumulatedDelay));
                        frameDuration[i] = acumulatedDelay;
                        rawFrames[i] = Encoding.UTF8.GetBytes(sb.ToString());
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

                        RenderFrame(ref bytes, ref sb, ref config);

                        rawFrames[0] = Encoding.UTF8.GetBytes(sb.ToString());
                    }
                }
                return (frameDuration, rawFrames);
            }
        }

        private static void RenderFrame(ref byte[] buffer, ref StringBuilder sb, ref ConversorConfig config)
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

                            sb.Append(AnsiHelper.BRGB(predictedChar, r, g, b));
                        }
                        else
                        {
                            sb.Append(AnsiHelper.FRGB(predictedChar, r, g, b));
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
    }
}