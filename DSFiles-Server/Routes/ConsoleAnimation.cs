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

       }

        private static async void HandleInformation(HttpRequest req, HttpResponse res)
		{
			
			string html= @"<html lang=""en"">
	<head>
		<meta charset=""UTF-8"" />
		<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
		<title>Console Animation Builder</title>*
		<style>
			body {
				font-family: 'Segoe UI', sans-serif;
				background: #1e1e1e;
				color: #d4d4d4;
				max-width: 800px;
				margin: 0 auto;
				padding: 20px;
			}
			h2 {
				border-bottom: 1px solid #3c3c3c;
				padding-bottom: 10px;
				color: #569cd6;
			}
			.form-group {
				margin-bottom: 15px;
				display: flex;
				align-items: center;
				justify-content: space-between;
			}
			label {
				width: 200px;
				font-weight: 600;
				color: #9cdcfe;
			}
			input[type='text'],
			input[type='number'] {
				padding: 8px;
				border-radius: 4px;
				border: 1px solid #3c3c3c;
				background: #252526;
				color: #ce9178;
				width: 100%;
				max-width: 400px;
				font-family: 'Consolas', monospace;
			}
			input:focus {
				outline: 1px solid #0078d4;
			}
			input[type='checkbox'] {
				transform: scale(1.5);
				margin-right: 10px;
				cursor: pointer;
			}
			.desc {
				font-size: 0.8em;
				color: #6a9955;
				margin-left: 10px;
				display: block;
				margin-top: 4px;
			}
			.options-container {
				display: flex;
				flex-direction: column;
				gap: 8px;
			}
			.output-area {
				background: #000;
				padding: 20px;
				border-radius: 5px;
				border: 1px solid #333;
				margin-top: 30px;
			}
			code {
				color: #b5cea8;
				font-family: 'Consolas', monospace;
				word-break: break-all;
				font-size: 0.9em;
			}
			button {
				background: #0e639c;
				color: white;
				border: none;
				padding: 10px 20px;
				cursor: pointer;
				border-radius: 4px;
				margin-top: 15px;
				font-weight: bold;
			}
			button:hover {
				background: #1177bb;
			}
		</style>
	</head>
	<body>
		<h2>Console Animation Generator</h2>

		<div class=""form-group"">
			<div>
				<label>Media URL</label>
				<span class=""desc"">The GIF or Image URL</span>
			</div>
			<input
				type=""text""
				id=""url""
				placeholder=""https://exmple.com/animation.gif""
				oninput=""saveAndGen()""
			/>
		</div>

		<div class=""form-group"">
			<div>
				<label>Width Divisor</label>
				<span class=""desc"">Horizontal Divisor Multiplier</span>
			</div>
			<!-- Regex removes anything that is not a digit -->
			<input
				type=""number""
				id=""wd""
				min=""1""
				value=""1""
				oninput=""
					this.value = this.value.replace(/[^0-9]/g, '');
					saveAndGen();
				""
			/>
		</div>

		<div class=""form-group"">
			<div>
				<label>Height Divisor</label>
				<span class=""desc"">Vertical Divisor Multiplier</span>
			</div>
			<input
				type=""number""
				id=""hd""
				min=""1""
				value=""1""
				oninput=""
					this.value = this.value.replace(/[^0-9]/g, '');
					saveAndGen();
				""
			/>
		</div>

		<div class=""form-group"">
			<div>
				<label>Compression</label>
				<span class=""desc"">Min RGB val change threshold</span>
			</div>
			<input
				type=""number""
				id=""mccn""
				min=""0""
				max=""765""
				value=""10""
				oninput=""
					this.value = this.value.replace(/[^0-9]/g, '');
					saveAndGen();
				""
			/>
		</div>

		<div class=""form-group"">
			<div>
				<label>FPS Divisor</label>
				<span class=""desc"">Divide frames per second</span>
			</div>
			<input
				type=""number""
				id=""fps""
				min=""1""
				placeholder=""Default""
				oninput=""
					this.value = this.value.replace(/[^0-9]/g, '');
					saveAndGen();
				""
			/>
		</div>

		<div class=""form-group"">
			<div>
				<label>Offset (ms)</label>
				<span class=""desc"">Timing offset (Negatives allowed)</span>
			</div>
			<!-- Regex allows digits and the minus sign -->
			<input
				type=""number""
				id=""offset""
				placeholder=""e.g. 30 or -60""
				oninput=""
					this.value = this.value.replace(/[^0-9-]/g, '');
					saveAndGen();
				""
			/>
		</div>

		<div class=""form-group"">
			<label>Other Options</label>
			<div class=""options-container"">
				<div>
					<input
						type=""checkbox""
						id=""pixel""
						checked
						onchange=""saveAndGen()""
					/>
					Pixel Mode (Paint block)
				</div>
				<div>
					<input type=""checkbox"" id=""hdr"" onchange=""saveAndGen()"" />
					HDR (Maximize RGB colors)
				</div>
				<div>
					<input type=""checkbox"" id=""i"" onchange=""saveAndGen()"" />
					Invert Colors
				</div>
			</div>
		</div>

		<div class=""output-area"">
			<div style=""color: #ccc; margin-bottom: 10px"">
				<strong>Generated Command:</strong>
			</div>
			<code id=""cmd"">...</code>
			<br />
			<button onclick=""copyCmd()"">Copy cURL</button>
		</div>

		<script>
			const textInputs = ['url', 'wd', 'hd', 'mccn', 'fps', 'offset'];
			const checkInputs = ['pixel', 'hdr', 'i'];

			function loadSettings() {
				textInputs.forEach((id) => {
					const val = localStorage.getItem('anim_' + id);
					if (val !== null) document.getElementById(id).value = val;
				});

				checkInputs.forEach((id) => {
					const val = localStorage.getItem('anim_' + id);
					if (val !== null)
						document.getElementById(id).checked = val === 'true';
				});

				gen();
			}

			function saveAndGen() {
				textInputs.forEach((id) => {
					localStorage.setItem(
						'anim_' + id,
						document.getElementById(id).value
					);
				});

				checkInputs.forEach((id) => {
					localStorage.setItem(
						'anim_' + id,
						document.getElementById(id).checked
					);
				});

				gen();
			}

			function gen() {
				const baseUrl = 'https://df.gato.ovh/animate';

				let params = new URLSearchParams();

				const getVal = (id) => document.getElementById(id).value;
				const getCheck = (id) => document.getElementById(id).checked;

				if (getVal('wd')) params.append('wd', getVal('wd'));
				if (getVal('hd')) params.append('hd', getVal('hd'));

				params.append('hdr', getCheck('hdr'));
				params.append('pixel', getCheck('pixel'));

				if (getCheck('i')) params.append('i', 'true');

				if (getVal('mccn')) params.append('mccn', getVal('mccn'));
				if (getVal('fps')) params.append('fps', getVal('fps'));
				if (getVal('offset')) params.append('offset', getVal('offset'));

				if (getVal('url')) params.append('url', getVal('url'));

				const finalUrl = `${baseUrl}?${params.toString()}`;

				const curlCmd = `curl ""${finalUrl}"" -v -k`;

				document.getElementById('cmd').innerText = curlCmd;
			}

			function copyCmd() {
				const text = document.getElementById('cmd').innerText;
				navigator.clipboard.writeText(text).then(() => {
					const btn = document.querySelector('button');
					const originalText = btn.innerText;
					btn.innerText = 'Copied!';
					setTimeout(() => (btn.innerText = originalText), 1500);
				});
			}

			// Initialize
			loadSettings();
		</script>
	</body>
</html>
";

            await res.WriteAsync(html);
        }
        public static async Task HandleAnimation(HttpRequest req, HttpResponse res, CancellationToken token)
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

            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int i = 0; i < rawFrames.Length; i++)
                    {
                        var data = rawFrames[i];

                        await res.Body.WriteAsync(data, 0, data.Length, token);

                        if (rawFrames.Length == 1)
                        {
                            while (!token.IsCancellationRequested)
                            {
                                res.Body.Write(AnsiHelper.SetPosition(0, 0));

                                await Task.Delay(5 * 1000);
                            }
                        }

                        if (frameDuration.Count() > 0)
                        {
                            int timeToSleep = (int)(frameDuration[i] - sw.ElapsedMilliseconds) + timeOffset;

                            if (timeToSleep > 0)
                            {
                                await Task.Delay(Math.Abs(timeToSleep));
                                Console.WriteLine("Sleeping " + timeToSleep);
                            }
                        }
                        else
                        {
                            break;
                        }

                        sw.Restart();
                    }
                }
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
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
                    sb.Append(AnsiHelper.SetSize(transformedWidth + 1, transformedHeight + 2));
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