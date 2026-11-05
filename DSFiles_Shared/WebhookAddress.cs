namespace DSFiles_Shared
{
    public static class WebhookAddress
    {
        public static bool IsValid(string? value)
        {
            if (value == null || value.Any(char.IsControl)
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || uri.IdnHost is not ("discord.com" or "ptb.discord.com" or "canary.discord.com" or "discordapp.com" or "ptb.discordapp.com" or "canary.discordapp.com"))
                return false;
            var parts = uri.AbsolutePath.Split('/');
            int start = parts.Length > 2 && parts[2].Length > 1 && parts[2][0] == 'v'
                && parts[2][1..].All(char.IsAsciiDigit) ? 3 : 2;
            return parts.Length == start + 3 && parts[1] == "api" && parts[start] == "webhooks"
                && ulong.TryParse(parts[start + 1], out var id) && id != 0
                && parts[start + 2].Length is > 0 and <= 256
                && parts[start + 2].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        }
    }
}
