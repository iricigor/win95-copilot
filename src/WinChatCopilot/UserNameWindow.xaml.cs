using System.IO;
using System.Media;
using System.Windows;
using WinChatCopilot.Settings;

namespace WinChatCopilot;

public partial class UserNameWindow : RetroWindow
{
    private readonly Action<string> save;

    public UserNameWindow(string currentName, Action<string> save)
    {
        this.save = save;
        InitializeComponent();
        NameInput.Text = currentName;
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            save(UserDisplayName.Normalize(NameInput.Text));
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ValidationMessage.Text = error.Message;
            SystemSounds.Hand.Play();
        }
    }
}
