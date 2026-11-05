using DSFiles_Server;
using DSFiles_Server.Routes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using System.Net.Quic;
using System.Text.Json;

namespace DSFiles_Tests
{
    internal static class Program
    {
        private static async Task Main()
        {
            if (!QuicListener.IsSupported)
            {
                throw new InvalidOperationException("QUIC native dependencies are unavailable.");
            }

            Console.WriteLine("PASS: QUIC native library loaded");
            var response = new DefaultHttpContext().Response;
            response.Body = new MemoryStream();
            await response.WriteAsJsonAsync(new ProofChallengeResponse(new string('a', 48), 20, 123), AppJsonContext.Default.ProofChallengeResponse);
            response.Body.Position = 0;
            using (var document = await JsonDocument.ParseAsync(response.Body))
            {
                Require(document.RootElement.GetProperty("difficulty").GetInt32() == 20, "HTTP JSON contract");
            }

            Console.WriteLine("PASS: source-generated HTTP JSON");
            string folder = Path.Combine(Path.GetTempPath(), "dsfiles-self-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "queue.json");
                var queue = new ShortLinkExpiryQueue(NullLogger<ShortLinkExpiryQueue>.Instance, path);
                await queue.Add("abc123", "jsp:abc123:" + new string('a', 48), DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
                using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
                {
                    Require(saved.RootElement[0].TryGetProperty("Key", out _), "Legacy queue JSON field names");
                }

                var restored = new ShortLinkExpiryQueue(NullLogger<ShortLinkExpiryQueue>.Instance, path);
                Require(await restored.Expired("abc123"), "Persisted expiry queue");
                await restored.Forget("abc123");
                var cleared = new ShortLinkExpiryQueue(NullLogger<ShortLinkExpiryQueue>.Instance, path);
                Require(!await cleared.Expired("abc123"), "Persisted queue removal");
                Console.WriteLine("PASS: expiry queue saved, restored and updated");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }

            Environment.SetEnvironmentVariable("SIGNKEY", new string('s', 64));
            Environment.SetEnvironmentVariable("ENCRYPTKEY", new string('e', 32));
            const string payload = "{\"seed\":\"native-test\",\"name\":\"file.txt\"}";
            string token = JWTManager.CreateSecureToken(payload);
            string? content = JWTManager.ProcessReturnedToken(token);
            Require(token.Split('.').Length == 5 && content != null && JsonSerializer.Deserialize(content, AppJsonContext.Default.String) == payload, "Encrypted JWT round trip");
            Console.WriteLine("PASS: encrypted and signed JWT round trip");
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Red);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var decoded = SKBitmap.Decode(encoded);
            Require(decoded != null && decoded.Width == 2 && decoded.GetPixel(0, 0) == SKColors.Red, "Skia image round trip");
            Console.WriteLine("PASS: SkiaSharp PNG encode/decode");
            await ChunkRetryTests.Run();
            await QuicRelayTests.Run();
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message + " failed.");
            }
        }
    }
}
