using DSFiles_Shared;
using System.Text.Json.Nodes;

namespace DSFiles_Server.Helpers
{
    internal static class UploadReceipt
    {
        internal static string Protect(string token) => "srv:" + JWTManager.CreateDeletionToken(token) + ":v2";
        internal static string Unprotect(string token)
        {
            var parts = token.Split(':');
            if (parts.Length != 3 || parts[0] != "srv" || parts[2] != "v2")
                throw new System.Security.Cryptography.CryptographicException();
            return JWTManager.ReadDeletionToken(parts[1]);
        }

        internal static void EnsureWebhookKeys(string webhook)
        {
            string? configured = Environment.GetEnvironmentVariable("WEBHOOK");
            if (!WebhookAddress.IsValid(configured) || !WebhookAddress.IsValid(webhook))
                return;
            if (new Uri(configured!).AbsolutePath.Split('/').TakeLast(2).SequenceEqual(new Uri(webhook).AbsolutePath.Split('/').TakeLast(2)))
                JWTManager.EnsureDeletionKeys();
        }

        internal static string Json(DiscordFilesSpliter.Upload upload, bool protectCredential = false)
        {
            string token = upload.RemoveToken;
            string? configured = Environment.GetEnvironmentVariable("WEBHOOK");
            if (protectCredential)
                token = Protect(token);
            else if (WebhookAddress.IsValid(configured))
            {
                var parts = new Uri(configured!).AbsolutePath.Split('/');
                if (parts[^2] == upload.WebHook.id.ToString() && parts[^1] == upload.WebHook.token)
                    token = Protect(token);
            }
            return new JsonObject
            {
                ["name"] = upload.FileName,
                ["seed"] = upload.SeedString,
                ["removeToken"] = token,
                ["webLink"] = upload.WebLink
            }.ToJsonString();
        }
    }
}
