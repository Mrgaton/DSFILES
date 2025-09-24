using System;
using System.Text.RegularExpressions;

public static class AnsiStrip
{
    // Multi-line, commented pattern. We use IgnorePatternWhitespace so comments are allowed.
    private const string AnsiPattern = @"
    (?:
        \x1B\[[0-?]*[ -/]*[@-~]         # CSI ... cmd
      | \x9B[0-?]*[ -/]*[@-~]           # 8-bit CSI
      | \x1B\][\s\S]*?(?:\x07|\x1B\\)   # OSC ... BEL or ST
      | \x9D[\s\S]*?\x9C                # 8-bit OSC
      | \x1BP[\s\S]*?\x1B\\             # DCS ... ESC P ... ESC \
      | \x90[\s\S]*?\x9C                # 8-bit DCS
      | \x1B[\^_][\s\S]*?\x1B\\         # SOS/PM/APC ... ST
      | [\x80-\x8F\x91-\x97\x98-\x9A]   # other C1 single bytes (rare)
      | \x1B[@-Z\\-_]                   # 2-char ESC sequences
    )";

    // Remove most non-printable control chars except CR/LF/TAB
    private static readonly Regex ControlRegex = new Regex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]",
                                                           RegexOptions.Compiled);

    private static readonly Regex AnsiRegex = new Regex(
        AnsiPattern,
        RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled
    );

    /// <summary>Strip ANSI sequences completely (returns empty string if nothing left).</summary>
    public static string Strip(string? s)
        => s is null ? string.Empty : AnsiRegex.Replace(s, "");

    /// <summary>
    /// Strip ANSI sequences and clean remaining non-printable controls (keeps \r \n \t).
    /// </summary>
    public static string StripAndClean(string? s)
    {
        if (s is null) return string.Empty;
        var stripped = AnsiRegex.Replace(s, "");
        return ControlRegex.Replace(stripped, "");
    }

    /// <summary>
    /// Strip ANSI sequences and return null if the final result is empty or whitespace.
    /// ("to null" behaviour you asked for)
    /// </summary>
    public static string? StripToNullIfEmpty(string? s)
    {
        if (s is null) return null;
        var stripped = AnsiRegex.Replace(s, "");
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    /// <summary>Replace each ANSI sequence with the NUL character (rarely useful).</summary>
    public static string StripToNulChar(string? s)
        => s is null ? string.Empty : AnsiRegex.Replace(s, "\0");
}
