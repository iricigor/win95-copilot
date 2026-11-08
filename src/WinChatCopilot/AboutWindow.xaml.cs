namespace WinChatCopilot;

public partial class AboutWindow : RetroWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {ApplicationInfo.Version}";
    }
}
