using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace DSFiles_Server
{
    internal class ConsoleAnimation
    {
        private static int maxBrightness = 256 * 3;
        private static int colorLessMinCharSetLengh = 1;

        private static int minColorChangeNeeded = 1;

        private static char[] charSet = " .,:;i1tfLCOG08@#".ToCharArray();

        private static int blankBrightNess = ((maxBrightness) / charSet.Length) * colorLessMinCharSetLengh;

        public static void HandleAnimation(HttpListenerRequest req, HttpListenerResponse res)
        {
            res.AddHeader("Cache-Control", "no-cache, no-store, no-transform");
            res.SendChunked = true;

            string url = req.QueryString["link"] ?? req.QueryString["l"] ?? req.QueryString["u"];

            if (string.IsNullOrWhiteSpace(url))
            {
                res.Send("Please set the query to a photo or gif");
                return;
            }

            using (Image gifImage = Image.FromStream(new WebClient().OpenRead(url)))
            {
                int width = gifImage.Width, height = gifImage.Height;

                int fpsDivisor = Math.Max(1, int.Parse(req.QueryString["fd"] ?? "1"));

                int widthDivisor = int.Parse(req.QueryString["wd"] ?? "1");
                int heightDivisor = int.Parse(req.QueryString["hd"] ?? "1") * 2;

                StringBuilder isb = new StringBuilder();

                isb.Append($"\u001b]0;titulo rancio\u0007");
                isb.Append($"\u001b[8;{(gifImage.Height / heightDivisor) + 2};{(gifImage.Width / widthDivisor) + 1}t");

                var d = Encoding.UTF8.GetBytes(isb.ToString());
                res.OutputStream.Write(d, 0, d.Length);

                FrameDimension dimension = new FrameDimension(gifImage.FrameDimensionsList[0]);

                int frameCount = gifImage.GetFrameCount(dimension);

                Stopwatch sw = new Stopwatch();

                sw.Start();

                while (true)
                {
                    for (int i = 0; i < frameCount; i += fpsDivisor)
                    {
                        gifImage.SelectActiveFrame(dimension, i);

                        StringBuilder sb = new StringBuilder();

                        sb.Append(AsciiHelper.SetPosition(0, 0));

                        using (Bitmap frame = new Bitmap(gifImage.Width, gifImage.Height, gifImage.PixelFormat))
                        {
                            using (Graphics g = Graphics.FromImage(frame))
                            {
                                g.DrawImage(gifImage, Point.Empty);
                            }

                            Rectangle rect = new Rectangle(0, 0, frame.Width, frame.Height);
                            BitmapData bmpData = frame.LockBits(rect, ImageLockMode.ReadOnly, frame.PixelFormat);

                            int stride = bmpData.Stride;

                            byte[] rgbValues = new byte[Math.Abs(bmpData.Stride) * frame.Height];
                            Marshal.Copy(bmpData.Scan0, rgbValues, 0, rgbValues.Length);
                            frame.UnlockBits(bmpData);

                            byte lastR = 0, lastG = 0, lastB = 0;

                            for (int y = 0; y < height; y += heightDivisor)
                            {
                                if (y > height) break;

                                for (int x = 0; x < width; x += widthDivisor)
                                {
                                    if (x > width) break;

                                    int index = y * stride + x * 4;

                                    byte r = rgbValues[index], g = rgbValues[index + 1], b = rgbValues[index + 2];

                                    int brightness = r + g + b;

                                    char predictedChar = charSet[(brightness * charSet.Length) / maxBrightness];

                                    //MaximizeBrightness(ref pixelValue.Item2, ref pixelValue.Item1, ref pixelValue.Item0);
                                    //QuantinizePixel(ref pixelValue.Item2, ref pixelValue.Item1, ref pixelValue.Item0);

                                    int colorDiff = Math.Abs(r - lastR) + Math.Abs(g - lastG) + Math.Abs(b - lastB);

                                    if (brightness > blankBrightNess && colorDiff > minColorChangeNeeded) //Dont change the color if its too similar to the current one
                                    {
                                        sb.Append(AsciiHelper.Pastel(predictedChar, r, g, b));

                                        lastR = r;
                                        lastG = g;
                                        lastB = b;
                                    }
                                    else
                                    {
                                        sb.Append(predictedChar);
                                    }
                                }

                                sb.Append('\n');
                            }
                        }

                        //Console.WriteLine(sb.ToString());

                        var data = Encoding.UTF8.GetBytes(sb.ToString());
                        res.OutputStream.Write(data, 0, data.Length);

                        PropertyItem item = gifImage.GetPropertyItem(0x5100);

                        int delay = BitConverter.ToInt32(item.Value, i * 4) * 10;

                        Console.WriteLine($"Frame {i}: Delay = {delay} ms");

                        int timeToSleep = (int)(sw.ElapsedMilliseconds - (delay * fpsDivisor));

                        if (timeToSleep < 0)
                        {
                            Console.WriteLine("Sleeping " + timeToSleep);
                            Thread.Sleep(Math.Abs(timeToSleep));
                        }

                        sw.Restart();
                    }
                }
            }
        }

        public static void SendFrame(ref byte[] data, ref HttpListenerResponse res)
        {
        }

        internal class AsciiHelper
        {
            public static string SetSize(int x, int y) => ("\u001b[8;" + y + ";" + x + "t");

            public static string ResetColor() => ("\u001b[0m");

            //private static string Pastel(string text, int r, int g, int b) => "\u001b[38;2;" + r + ";" + g + ";" + b + "m" + text;
            public static string Pastel(char c, int r, int g, int b) => "\u001b[38;2;" + r + ";" + g + ";" + b + "m" + c;

            public static string SetPosition(int row, int collum) => "\u001b[" + row + ";" + collum + "H";
        }
    }
}