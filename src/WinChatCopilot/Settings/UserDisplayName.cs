using System.IO;

namespace WinChatCopilot.Settings;

internal static class UserDisplayName
{
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Enter a user name containing visible text.");
        if (value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new InvalidDataException("Use a single-line user name without line breaks, tabs, or control characters.");
        return value.Trim();
    }
}
