using System.Windows.Input;

namespace WinChatCopilot;

public static class ShellCommands
{
    public static RoutedUICommand About { get; } = Create("About");
    public static RoutedUICommand SystemMenu { get; } = Create("System menu");
    public static RoutedUICommand Move { get; } = Create("Move");
    public static RoutedUICommand Size { get; } = Create("Size");
    public static RoutedUICommand SaveTranscript { get; } = Create("Save transcript");
    public static RoutedUICommand ClearWindow { get; } = Create("Clear window");
    public static RoutedUICommand Copy { get; } = ApplicationCommands.Copy;
    public static RoutedUICommand Paste { get; } = ApplicationCommands.Paste;
    public static RoutedUICommand SelectAll { get; } = ApplicationCommands.SelectAll;
    public static RoutedUICommand SendWithEnter { get; } = Create("Send with Enter");
    public static RoutedUICommand SendWithControlEnter { get; } = Create("Send with Ctrl+Enter");
    public static RoutedUICommand WordWrap { get; } = Create("Word wrap");
    public static RoutedUICommand AutoConnect { get; } = Create("Auto-connect");
    public static RoutedUICommand ShowTimestamp { get; } = Create("Show timestamp");
    public static RoutedUICommand UserName { get; } = Create("User name");
    public static RoutedUICommand ConfigureBackend { get; } = Create("Configure backend");
    public static RoutedUICommand Connect { get; } = Create("Connect");
    public static RoutedUICommand Send { get; } = Create("Send");
    public static RoutedUICommand Stop { get; } = Create("Stop");

    public static IReadOnlyList<RoutedUICommand> Unimplemented { get; } = [];

    private static RoutedUICommand Create(string name) => new(name, name, typeof(ShellCommands));
}
