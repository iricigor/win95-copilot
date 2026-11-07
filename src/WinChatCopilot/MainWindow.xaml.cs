using System.Windows;

namespace WinChatCopilot;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"WinChat-Copilot {ApplicationInfo.Version}";
    }
}