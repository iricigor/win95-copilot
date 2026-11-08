using System.IO;
using System.Windows.Media;

namespace WinChatCopilot.Settings;

internal static class TranscriptFont
{
    public const string Default = "Courier New";
    public static IReadOnlyList<string> Choices { get; } =
        Array.AsReadOnly(new[] { Default, "Lucida Console", "Microsoft Sans Serif" });

    public static void Validate(string? name)
    {
        if (name is null || !Choices.Contains(name))
            throw new InvalidDataException("Unsupported text font. Choose Courier New, Lucida Console, or Microsoft Sans Serif in Options > Font.");
    }

    public static bool IsInstalled(string name) =>
        Fonts.SystemFontFamilies.Any(family => string.Equals(family.Source, name, StringComparison.OrdinalIgnoreCase));
}
