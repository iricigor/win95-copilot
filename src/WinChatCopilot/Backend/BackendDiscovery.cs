using System.IO;

namespace WinChatCopilot.Backend;

public sealed class BackendDiscovery(string? searchPath, IReadOnlyList<string> packageRoots)
{
    public static BackendDiscovery CreateDefault() => new(
        Environment.GetEnvironmentVariable("PATH"),
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages")
        ]);

    public string Resolve(string? explicitPath)
    {
        if (explicitPath is not null)
            return ValidateExplicit(explicitPath);

        foreach (var directory in (searchPath ?? "").Split(Path.PathSeparator))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            var candidate = Path.Combine(trimmed, "copilot.exe");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        foreach (var root in packageRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root, "GitHub.Copilot_*")
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory, "copilot.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("Copilot was not found on PATH or in the default WinGet package roots. Install GitHub Copilot CLI separately, then select its actual copilot.exe in Backend Settings. Custom installation roots require an explicit selection.");
    }

    public static string ValidateExplicit(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new FileNotFoundException($"The selected backend '{path}' is not an existing absolute executable path. Select the actual Copilot .exe; this selection will not be silently replaced.");
        return Path.GetFullPath(path);
    }
}
