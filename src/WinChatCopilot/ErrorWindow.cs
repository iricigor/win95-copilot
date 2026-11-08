using System.Media;
using System.Windows;
using System.Windows.Controls;

namespace WinChatCopilot;

public sealed class ErrorWindow : RetroWindow
{
    public ErrorWindow(string message)
    {
        Resources = new ResourceDictionary { Source = new Uri("/WinChatCopilot;component/RetroTheme.xaml", UriKind.Relative) };
        SetResourceReference(StyleProperty, "RetroWindowStyle");
        Title = "WinChat-Copilot Error";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        });
        var ok = new Button
        {
            Content = "_OK", Width = 80, IsDefault = true, IsCancel = true,
            Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
        };
        ButtonIcon.SetSource(ok, (System.Windows.Media.ImageSource)Resources["ConfirmIcon"]);
        ok.Click += (_, _) => DialogResult = true;
        panel.Children.Add(ok);
        Content = panel;
        Loaded += (_, _) => SystemSounds.Hand.Play();
    }
}
