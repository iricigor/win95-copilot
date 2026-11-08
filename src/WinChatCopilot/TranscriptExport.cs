using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace WinChatCopilot;

internal static class TranscriptExport
{
    internal static SaveFileDialog CreateDialog()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Transcript",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = "Transcript.txt",
            CheckPathExists = true,
            OverwritePrompt = true
        };
        if (Environment.GetEnvironmentVariable("WINCHAT_DATA_DIRECTORY") is { } isolatedDirectory)
        {
            dialog.InitialDirectory = isolatedDirectory;
            dialog.AddToRecent = false;
            dialog.ClientGuid = Guid.NewGuid();
        }
        return dialog;
    }

    internal static string? SelectPath(Window owner)
    {
        var dialog = CreateDialog();
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    internal static async Task WriteAsync(string path, string snapshot, CancellationToken cancellationToken)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException("Choose an absolute destination path.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, snapshot, new UTF8Encoding(false, true), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IOException($"Cannot save transcript at '{path}'. Check the destination, file permissions and disk space, then retry. Your conversation has not been changed. {error.Message}", error);
        }
    }
}
