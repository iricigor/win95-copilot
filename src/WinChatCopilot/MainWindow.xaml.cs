using System.Windows;
using System.Windows.Input;

namespace WinChatCopilot;

public partial class MainWindow : RetroWindow
{
    public MainWindow()
    {
        foreach (var command in ShellCommands.Unimplemented)
            CommandBindings.Add(new CommandBinding(command, null, (_, e) => e.CanExecute = false));
        CommandBindings.Add(new CommandBinding(ShellCommands.About,
            (_, _) => new AboutWindow { Owner = this }.ShowDialog(), (_, e) => e.CanExecute = true));
        InitializeComponent();
        Title = $"WinChat-Copilot {ApplicationInfo.Version}";
    }
}