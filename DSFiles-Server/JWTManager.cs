using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DSFiles_Server
{
    internal static class JWTManager
    {
        private static string? SignKey = Environment.GetEnvironmentVariable("SIGNKEY") ?? null;

        private static string? EncryptKey = Environment.GetEnvironmentVariable("ENCRYPTKEY") ?? null;

        public static bool JWTEnabled = !string.IsNullOrEmpty(SignKey) && !string.IsNullOrEmpty(EncryptKey);
        public static string CreateSecureToken(object myJsonData)
        {
            var handler = new JwtSecurityTokenHandler();

            string jsonString = JsonSerializer.Serialize(myJsonData);

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
