using System.IO;
using System.Media;
using System.Windows;
using Microsoft.Win32;
using WinChatCopilot.Backend;

namespace WinChatCopilot;

public partial class BackendSettingsWindow : RetroWindow
{
    private readonly BackendController backend;

    public BackendSettingsWindow(BackendController backend, string? guidance = null)
    {
        this.backend = backend;
        InitializeComponent();
        ExecutableInput.Text = backend.Settings.ExecutablePath ?? "";
        AutoConnectInput.IsChecked = backend.Settings.AutoConnect;
        ValidationMessage.Text = guidance ?? "";
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Executable files (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
            ExecutableInput.Text = dialog.FileName;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            backend.Save(backend.Settings with
            {
                ExecutablePath = string.IsNullOrWhiteSpace(ExecutableInput.Text) ? null : ExecutableInput.Text.Trim(),
                AutoConnect = AutoConnectInput.IsChecked == true
            });
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ValidationMessage.Text = error.Message;
            SystemSounds.Hand.Play();
        }
    }
}
