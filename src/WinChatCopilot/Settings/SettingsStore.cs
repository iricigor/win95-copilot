using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinChatCopilot.Settings;

public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string FilePath { get; } = Path.GetFullPath(filePath);
    public string DirectoryPath => Path.GetDirectoryName(FilePath)!;

    public AppSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, Options)
                ?? throw new InvalidDataException("The settings document is null.");
            Validate(settings);
            return settings;
        }
        catch (FileNotFoundException) { return new AppSettings(); }
        catch (DirectoryNotFoundException) { return new AppSettings(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            throw new IOException($"Cannot read settings at '{FilePath}'. Restore a valid settings file or use Backend Settings to explicitly save new preferences. {error.Message}", error);
        }
    }

    public void Save(AppSettings settings)
    {
        Validate(settings);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot save settings at '{FilePath}'. Check file permissions and disk space, then retry. Your previous preferences have not been replaced. {error.Message}", error);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void Validate(AppSettings settings)
    {
        UserDisplayName.Normalize(settings.UserName);
        TranscriptFont.Validate(settings.TextFont);
        if (settings.SchemaVersion != 1 || !Enum.IsDefined(settings.SendMode) ||
            settings.ExecutablePath is not null && string.IsNullOrWhiteSpace(settings.ExecutablePath))
            throw new InvalidDataException("Unsupported settings version, send mode, or empty executable path. Use null for automatic discovery.");
    }
}
