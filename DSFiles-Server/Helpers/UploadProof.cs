using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace DSFiles_Server
{
    internal static class UploadProof
    {
        private const int Difficulty = 20;
        private static string Binding(HttpRequest request, long size) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "upload\n" + request.Headers["filename"] + "\n" + size.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n" + request.Headers["webhook"]
            + (request.Headers.ContainsKey("lifetime") ? "\nlifetime:" + request.Headers["lifetime"] : ""))));
        private static string DeleteBinding(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("delete\n" + token)));

        public static async Task Issue(HttpContext context)
        {
            var request = context.Request;
            if (request.Headers["filename"].ToString() is not { Length: > 0 and <= 64 }
                || !long.TryParse(request.Headers["filesize"], out long size) || size <= 0
                || request.Headers["webhook"].ToString().Length > 256)
            {
                context.Response.StatusCode = 400;
                return;
            }
            await IssueBound(context, Binding(request, size), false);
        }

        public static async Task IssueDelete(HttpContext context)
        {
            string token = context.Request.Headers["Remove-Token"].ToString();
            if (token.Length is < 5 or > 60000 || token.Split(':').Length != 3)
            {
                context.Response.StatusCode = 400;
                return;
            }
            await IssueBound(context, DeleteBinding(token), true);
        }

        private static async Task IssueBound(HttpContext context, string binding, bool delete)
        {
            try
            {
                JWTManager.EnsureDeletionKeys();
            }
            catch (InvalidOperationException) { context.Response.StatusCode = 503; return; }
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            string challenge = delete ? JWTManager.CreateDeletionChallenge(binding, expires) : JWTManager.CreateUploadChallenge(binding, expires);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new ProofChallengeResponse(challenge, Difficulty, expires.ToUnixTimeMilliseconds()),
                AppJsonContext.Default.ProofChallengeResponse, cancellationToken: context.RequestAborted);
        }

        public static bool? Consume(HttpRequest request, long size) => !request.Headers.ContainsKey("Upload-Proof")
            ? null : Verify(request.Headers["Upload-Proof"].ToString(), Binding(request, size), false);
        public static bool ConsumeDelete(HttpRequest request, string token) => Verify(request.Headers["Delete-Proof"].ToString(), DeleteBinding(token), true);

        private static bool Verify(string proof, string binding, bool delete)
        {
            var parts = proof.Split(':');
            if (proof.Length > 80 || parts.Length != 2 || parts[1].Length is < 1 or > 16 || !parts[1].All(char.IsAsciiDigit))
                return false;
            try
            {
                if (!(delete ? JWTManager.ValidateDeletionChallenge(parts[0], binding) : JWTManager.ValidateUploadChallenge(parts[0], binding)))
                    return false;
            }
            catch (InvalidOperationException) { return false; }
            byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(proof));
            for (int bit = 0; bit < Difficulty; bit++)
                if ((hash[bit / 8] & (1 << (7 - bit % 8))) != 0)
                    return false;
            return true;
        }
    }
}
