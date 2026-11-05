using System.Text.Json;
using System.Text.Json.Serialization;
using DSFiles_Server.Routes;

namespace DSFiles_Server
{
    internal sealed record ProofChallengeResponse(string Challenge, int Difficulty, long ExpiresAt);
    internal sealed record ApiErrorResponse(string Error);
    internal sealed record RemovalResponse(int Removed, int Total);
    internal sealed record PartialRemovalResponse(string Error, int Removed, int Total);
    internal sealed record ShortLinkResponse(string Key, string RemoveToken, long ExpiresAt);
    internal sealed record ShortLinkRecoveryResponse(string Error, string Key, string RemoveToken);
    internal sealed record CertificateResponse(byte[] PublicKey, string Subject, string Issuer, DateTime Expiration);
    internal sealed record ChunkSession(string UploadId, string FileName, long FileSize, byte[]? Key, string WebhookUrl,
        ulong WebhookId, string WebhookToken, ulong ChannelId, long ExpiresAt);
    internal sealed record ChunkReceipt(string UploadId, int Index, ulong AttachmentId, ulong MessageId, long Size);
    internal sealed record ChunkResponse(string Receipt);
    internal sealed record ChunkManifest(string[] Receipts);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ProofChallengeResponse))]
    [JsonSerializable(typeof(ApiErrorResponse))]
    [JsonSerializable(typeof(RemovalResponse))]
    [JsonSerializable(typeof(PartialRemovalResponse))]
    [JsonSerializable(typeof(ShortLinkResponse))]
    [JsonSerializable(typeof(ShortLinkRecoveryResponse))]
    [JsonSerializable(typeof(CertificateResponse))]
    [JsonSerializable(typeof(List<ShortLinkExpiryQueue.Item>))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(ChunkSession))]
    [JsonSerializable(typeof(ChunkReceipt))]
    [JsonSerializable(typeof(ChunkResponse))]
    [JsonSerializable(typeof(ChunkManifest))]
    internal partial class AppJsonContext : JsonSerializerContext
    {
        public static AppJsonContext PreserveNames { get; } = new(new JsonSerializerOptions());
    }
}
