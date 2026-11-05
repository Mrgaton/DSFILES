using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace DSFiles_Server
{
    internal static class JWTManager
    {
        // Read environment-based keys on first use, after Program has loaded .env.
        static JWTManager()
        {
        }
        private static string? SignKey =
            Environment.GetEnvironmentVariable("SIGNKEY") ?? null;

        private static string? EncryptKey =
            Environment.GetEnvironmentVariable("ENCRYPTKEY") ?? null;

        public static bool JWTEnabled
        {
            get =>
                !string.IsNullOrEmpty(SignKey) && !string.IsNullOrEmpty(EncryptKey);
        }

        internal static void EnsureDeletionKeys() => DeletionKeys();

        private static (SymmetricSecurityKey Sign, SymmetricSecurityKey Encrypt) DeletionKeys()
        {
            byte[] sign = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("SIGNKEY") ?? "");
            byte[] encrypt = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("ENCRYPTKEY") ?? "");
            if (sign.Length < 32 || encrypt.Length != 32)
                throw new InvalidOperationException("Deletion tokens require SIGNKEY of at least 32 UTF-8 bytes and ENCRYPTKEY of exactly 32 UTF-8 bytes.");
            return (new(sign), new(encrypt));
        }

        internal static string CreateDeletionToken(string content)
            => CreateEncryptedToken(content, "DSFiles:file-removal:v2", "delete-file", null);

        internal static string CreateUploadSession(string content, DateTimeOffset expires)
            => CreateEncryptedToken(content, "DSFiles:upload-session:v2", "upload-session", expires);

        private static string CreateEncryptedToken(string content, string audience, string purpose, DateTimeOffset? expires)
        {
            var keys = DeletionKeys();
            var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = "DSFiles",
                Audience = audience,
                Expires = expires?.UtcDateTime,
                Subject = new ClaimsIdentity([new Claim("purpose", purpose), new Claim("Content", content)]),
                SigningCredentials = new(keys.Sign, SecurityAlgorithms.HmacSha256),
                EncryptingCredentials = new(keys.Encrypt, SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512)
            };
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        internal static string ReadDeletionToken(string token)
            => ReadEncryptedToken(token, "DSFiles:file-removal:v2", "delete-file", false);

        internal static string ReadUploadSession(string token)
            => ReadEncryptedToken(token, "DSFiles:upload-session:v2", "upload-session", true);

        private static string ReadEncryptedToken(string token, string audience, string purpose, bool expires)
        {
            try
            {
                var keys = DeletionKeys();
                var handler = new JwtSecurityTokenHandler();
                if (token.Split('.').Length != 5)
                    throw new CryptographicException();
                var envelope = handler.ReadJwtToken(token);
                if (envelope.Header.Alg != SecurityAlgorithms.Aes256KW || envelope.Header.Enc != SecurityAlgorithms.Aes256CbcHmacSha512)
                    throw new CryptographicException();
                var principal = handler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "DSFiles",
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    IssuerSigningKey = keys.Sign,
                    TokenDecryptionKey = keys.Encrypt,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256, SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512],
                    RequireExpirationTime = expires,
                    ValidateLifetime = expires,
                    ClockSkew = TimeSpan.Zero
                }, out var validated);
                if (validated is not JwtSecurityToken jwt || jwt.InnerToken?.Header.Alg != SecurityAlgorithms.HmacSha256)
                    throw new CryptographicException();
                if (principal.FindFirst("purpose")?.Value != purpose)
                    throw new CryptographicException();
                return principal.FindFirst("Content")?.Value ?? throw new CryptographicException();
            }
            catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or FormatException or InvalidOperationException)
            {
                throw new CryptographicException("Invalid deletion token.", ex);
            }
        }

        internal static string CreateDeletionChallenge(string binding, DateTimeOffset expires)
            => CreateChallenge(binding, expires, "delete");

        internal static string CreateUploadChallenge(string binding, DateTimeOffset expires)
            => CreateChallenge(binding, expires, "upload");

        private static string CreateChallenge(string binding, DateTimeOffset expires, string purpose)
        {
            string payload = expires.ToUnixTimeSeconds().ToString("x8") + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            return payload + ChallengeMac(binding, payload, purpose);
        }

        internal static bool ValidateDeletionChallenge(string challenge, string binding)
            => ValidateChallenge(challenge, binding, "delete");

        internal static bool ValidateUploadChallenge(string challenge, string binding)
            => ValidateChallenge(challenge, binding, "upload");

        private static bool ValidateChallenge(string challenge, string binding, string purpose)
        {
            if (challenge.Length != 48 || !challenge.All(char.IsAsciiHexDigit)
                || !long.TryParse(challenge[..8], System.Globalization.NumberStyles.HexNumber, null, out long expires)
                || expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || expires > DateTimeOffset.UtcNow.AddMinutes(6).ToUnixTimeSeconds())
                return false;
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge[16..]), Convert.FromHexString(ChallengeMac(binding, challenge[..16], purpose)));
        }

        private static string ChallengeMac(string binding, string payload, string purpose)
        {
            var key = DeletionKeys().Sign.Key;
            return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("DSFiles:" + purpose + "-proof:v1\n" + binding + "\n" + payload)).AsSpan(0, 16)).ToLowerInvariant();
        }

        internal static string CreateChunkReceipt(string content, DateTimeOffset expires)
        {
            var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
            return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = "DSFiles",
                Audience = "DSFiles:chunk-receipt:v2",
                Expires = expires.UtcDateTime,
                Subject = new ClaimsIdentity([new Claim("purpose", "chunk-receipt"), new Claim("Content", content)]),
                SigningCredentials = new(DeletionKeys().Sign, SecurityAlgorithms.HmacSha256)
            }));
        }

        internal static string ReadChunkReceipt(string token)
        {
            try
            {
                if (token.Length > 2048 || token.Split('.').Length != 3)
                    throw new CryptographicException();
                var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "DSFiles",
                    ValidateAudience = true,
                    ValidAudience = "DSFiles:chunk-receipt:v2",
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    IssuerSigningKey = DeletionKeys().Sign,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.Zero
                }, out _);
                if (principal.FindFirst("purpose")?.Value != "chunk-receipt")
                    throw new CryptographicException();
                return principal.FindFirst("Content")?.Value ?? throw new CryptographicException();
            }
            catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or FormatException or InvalidOperationException)
            {
                throw new CryptographicException("Invalid chunk receipt.", ex);
            }
        }
        public static string CreateSecureToken(string myJsonData)
        {
            var handler = new JwtSecurityTokenHandler();

            string jsonString = JsonSerializer.Serialize(myJsonData, AppJsonContext.Default.String);

            if (EncryptKey.Length != 32)
            {
                throw new Exception("EncryptKey must be 32 characters long");
            }

            var signKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SignKey));
            var encKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(EncryptKey));

            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim("Content", jsonString)
                }),

                Expires = DateTime.UtcNow.AddHours(1),

                SigningCredentials = new SigningCredentials(
                    signKey,
                    SecurityAlgorithms.HmacSha256Signature),

                EncryptingCredentials = new EncryptingCredentials(
                    encKey,
                    SecurityAlgorithms.Aes256KW,
                    SecurityAlgorithms.Aes256CbcHmacSha512)
            };

            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        public static string? ProcessReturnedToken(string token)
        {
            var handler = new JwtSecurityTokenHandler();

            var signKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SignKey));
            var encKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(EncryptKey));

            try
            {
                var validationParams = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,

                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signKey,

                    TokenDecryptionKey = encKey,

                    ValidateLifetime = true
                };

                var principal = handler.ValidateToken(token, validationParams, out _);

                return principal.FindFirst("Content")?.Value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed: {ex.Message}");
                return null;
            }
        }
    }
}
