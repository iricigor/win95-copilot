using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace WinChatCopilot.Tests;

public class ShellTests
{
    [Fact]
    public void ShellLoadsSquareChromeAndRealTextAreas() => OnSta(() =>
    {
        var window = new MainWindow();
        try
        {
            window.ApplyTemplate();
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.NotNull(chrome);
            Assert.Equal(new CornerRadius(0), chrome.CornerRadius);
            Assert.Equal(new Thickness(0), chrome.GlassFrameThickness);
            Assert.False(chrome.UseAeroCaptionButtons);
            Assert.False(window.AllowsTransparency);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.Equal("Microsoft Sans Serif", window.FontFamily.Source);
            Assert.Equal(Color.FromRgb(192, 192, 192), ((SolidColorBrush)window.Background).Color);
            Assert.Null(window.Effect);

            var transcript = (TextBox)window.FindName("Transcript");
            var input = (TextBox)window.FindName("PromptInput");
            Assert.True(transcript.IsReadOnly);
            Assert.False(input.IsReadOnly);
            Assert.True(input.AcceptsReturn);
            Assert.Equal("Courier New", transcript.FontFamily.Source);
            Assert.Equal("Courier New", input.FontFamily.Source);
            Assert.Equal(Colors.White, ((SolidColorBrush)transcript.Background).Color);
            Assert.Contains("unavailable", ((TextBlock)window.FindName("BackendStatus")).Text);
            Assert.NotNull(window.Template.FindName("SystemMenuButton", window));
            Assert.NotNull(window.Template.FindName("MinimizeButton", window));
            Assert.NotNull(window.Template.FindName("MaximizeButton", window));
            Assert.Null(window.Template.FindName("CloseButton", window));
            Assert.Equal(Colors.White,
                ((SolidColorBrush)((Border)window.Template.FindName("TitleBar", window)).Background).Color);
            Assert.Equal(Colors.Black,
                ((TextBlock)window.Template.FindName("TitleLabel", window)).Foreground is SolidColorBrush title
                    ? title.Color : throw new InvalidOperationException("Title brush must be solid."));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SharedWindowStateCommandsUpdateStateAndEligibility() => OnSta(() =>
    {
        var window = new MainWindow();
        try
        {
            Assert.True(SystemCommands.MaximizeWindowCommand.CanExecute(null, window));
            Assert.False(SystemCommands.RestoreWindowCommand.CanExecute(null, window));
            SystemCommands.MaximizeWindowCommand.Execute(null, window);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.False(SystemCommands.MaximizeWindowCommand.CanExecute(null, window));
            Assert.False(ShellCommands.Move.CanExecute(null, window));
            Assert.False(ShellCommands.Size.CanExecute(null, window));
            Assert.True(SystemCommands.RestoreWindowCommand.CanExecute(null, window));
            SystemCommands.RestoreWindowCommand.Execute(null, window);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(ShellCommands.Move.CanExecute(null, window));
            Assert.True(ShellCommands.Size.CanExecute(null, window));
            SystemCommands.MinimizeWindowCommand.Execute(null, window);
            Assert.Equal(WindowState.Minimized, window.WindowState);
            Assert.False(SystemCommands.MinimizeWindowCommand.CanExecute(null, window));
            SystemCommands.RestoreWindowCommand.Execute(null, window);
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FutureActionsAreDisabledAndImplementedActionsUseSharedCommands() => OnSta(() =>
    {
        var window = new MainWindow();
        try
        {
            foreach (var command in ShellCommands.Unimplemented)
                Assert.False(command.CanExecute(null, window), command.Text);
            Assert.True(ShellCommands.About.CanExecute(null, window));
            Assert.True(SystemCommands.CloseWindowCommand.CanExecute(null, window));
            var menu = (Menu)window.FindName("MainMenu");
            Assert.Equal(new[] { "_File", "_Edit", "_Options", "_Help" },
                menu.Items.Cast<MenuItem>().Select(item => (string)item.Header));
            var actions = menu.Items.Cast<MenuItem>()
                .SelectMany(item => item.Items.OfType<MenuItem>()).ToArray();
            Assert.All(actions, item =>
            {
                Assert.NotNull(item.Command);
                Assert.Contains("_", (string)item.Header);
                item.CommandTarget = window;
                Assert.Equal(item.Command == ShellCommands.About ||
                    item.Command == SystemCommands.CloseWindowCommand, item.IsEnabled);
            });
            Assert.Same(ShellCommands.Send, ((Button)window.FindName("SendButton")).Command);
            Assert.Same(ShellCommands.Stop, ((Button)window.FindName("StopButton")).Command);
            Assert.False(((Button)window.FindName("SendButton")).IsEnabled);
            Assert.False(((Button)window.FindName("StopButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MinimumLayoutKeepsInputActionsAndStatusInsideContent() => OnSta(() =>
    {
        var window = new MainWindow();
        try
        {
            var content = (FrameworkElement)window.Content;
            var available = new Size(window.MinWidth - 10, window.MinHeight - 31);
            content.Measure(available);
            content.Arrange(new Rect(available));
            content.UpdateLayout();
            foreach (var name in new[] { "Transcript", "PromptInput", "SendButton", "StopButton", "BackendStatus" })
            {
                var element = (FrameworkElement)window.FindName(name);
                var origin = element.TranslatePoint(new Point(0, 0), content);
                Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, name);
                Assert.InRange(origin.X, 0, available.Width);
                Assert.InRange(origin.Y, 0, available.Height);
                Assert.True(origin.X + element.ActualWidth <= available.Width, name);
                Assert.True(origin.Y + element.ActualHeight <= available.Height, name);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AboutHasActualVersionDefaultCancelOkAndNoWindowSizing() => OnSta(() =>
    {
        var window = new AboutWindow();
        try
        {
            window.ApplyTemplate();
            Assert.Equal($"Version {ApplicationInfo.Version}", ((TextBlock)window.FindName("VersionLabel")).Text);
            Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(Visibility.Collapsed,
                ((Button)window.Template.FindName("MinimizeButton", window)).Visibility);
            Assert.Equal(Visibility.Collapsed,
                ((Button)window.Template.FindName("MaximizeButton", window)).Visibility);
            var ok = Assert.Single(((StackPanel)window.Content).Children.OfType<Button>());
            Assert.True(ok.IsDefault);
            Assert.True(ok.IsCancel);
            Assert.Same(SystemCommands.CloseWindowCommand, ok.Command);
            Assert.False(SystemCommands.MaximizeWindowCommand.CanExecute(null, window));
            Assert.False(SystemCommands.MinimizeWindowCommand.CanExecute(null, window));
            var closed = false;
            window.Closed += (_, _) => closed = true;
            SystemCommands.CloseWindowCommand.Execute(null, window);
            Assert.True(closed);
        }
        finally { window.Close(); }
    });

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Shell resource/action check timed out.");
        Assert.Null(failure);
    }
}
