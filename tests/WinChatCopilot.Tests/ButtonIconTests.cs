using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class ButtonIconTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void AllScopedActionsHaveAccessibleDecorativeIconsAndFitScaledLayouts(double scale) => OnSta(() =>
    {
        var controller = new BackendController(new SettingsStore(Path.Combine(Path.GetTempPath(),
            "winchat-icons-" + Guid.NewGuid(), "settings.json")), new BackendDiscovery("", []),
            (_, _) => throw new InvalidOperationException("Layout checks must not request a backend."));
        var windows = new RetroWindow[] { new MainWindow(), new BackendSettingsWindow(controller),
            new AboutWindow(), new ErrorWindow("Synthetic diagnostic."), new UserNameWindow("User", _ => { }) };
        try
        {
            var count = 0;
            foreach (var window in windows)
            {
                window.ApplyTemplate();
                var content = (FrameworkElement)window.Content;
                var width = window is MainWindow ? window.MinWidth - 10 : window.Width - 10;
                var height = window is MainWindow ? window.MinHeight - 31 :
                    window is ErrorWindow ? 180 : window.Height - 31;
                content.LayoutTransform = new ScaleTransform(scale, scale);
                var available = new Size(width * scale, height * scale);
                content.Measure(available);
                content.Arrange(new Rect(available));
                content.UpdateLayout();
                var buttons = Descendants(content).OfType<Button>().ToArray();
                Assert.Equal(window switch { MainWindow => 5, BackendSettingsWindow => 3, UserNameWindow => 2, _ => 1 },
                    buttons.Length);
                foreach (var button in buttons)
                {
                    count++;
                    AssertAction(window, button);
                    var label = Assert.Single(Descendants(button).OfType<AccessText>());
                    var icon = Assert.Single(Descendants(button).OfType<DecorativeIcon>());
                    var image = Assert.IsType<DrawingImage>(ButtonIcon.GetSource(button));
                    Assert.Same(image, icon.Source);
                    Assert.False(image.Drawing.Bounds.IsEmpty);
                    Assert.Equal(new Rect(0, 0, 16, 16), image.Drawing.Bounds);
                    Assert.Equal(16, icon.ActualWidth);
                    Assert.Equal(16, icon.ActualHeight);
                    Assert.False(icon.Focusable);
                    Assert.False(icon.IsHitTestVisible);
                    Assert.Null(UIElementAutomationPeer.CreatePeerForElement(icon));
                    Assert.Equal((string)button.Content, label.Text);
                    Assert.Contains("_", label.Text);
                    var peer = new ButtonAutomationPeer(button);
                    Assert.Equal(label.Text.Replace("_", ""), peer.GetName());
                    Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                    foreach (var element in new FrameworkElement[] { icon, label })
                    {
                        var origin = element.TranslatePoint(new Point(), button);
                        Assert.True(origin.X >= 0 && origin.Y >= 0, label.Text);
                        Assert.True(origin.X + element.ActualWidth <= button.ActualWidth + 0.1, label.Text);
                        Assert.True(origin.Y + element.ActualHeight <= button.ActualHeight + 0.1, label.Text);
                    }
                    var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Top >= 0, label.Text);
                    Assert.True(bounds.Right <= width + 0.1 && bounds.Bottom <= height + 0.1, label.Text);
                }
                // Scaled resource captures exercise rasterization, not desktop visual acceptance.
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(available.Width),
                    (int)Math.Ceiling(available.Height), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                Assert.Contains(pixels, pixel => pixel != 0);
                var evidence = Environment.GetEnvironmentVariable("WINCHAT_ICON_EVIDENCE_DIRECTORY");
                if (!string.IsNullOrEmpty(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(evidence, $"{window.GetType().Name}-{scale:0.0}.png"));
                    encoder.Save(file);
                }
            }
            Assert.Equal(12, count);
        }
        finally { foreach (var window in windows) window.Close(); }
    });

    [Fact]
    public void IconsAndLabelsSharePressOffsetDisabledStateAndFocusCueWithoutChangingChrome() => OnSta(() =>
    {
        var window = new AboutWindow();
        try
        {
            var button = new PressableButton { Content = "_OK", Resources = window.Resources };
            ButtonIcon.SetSource(button, (ImageSource)window.Resources["ConfirmIcon"]);
            button.Style = (Style)window.Resources[typeof(Button)];
            button.Measure(new Size(120, 40));
            button.Arrange(new Rect(0, 0, 120, 40));
            button.UpdateLayout();
            var icon = Assert.Single(Descendants(button).OfType<DecorativeIcon>());
            var label = (ContentPresenter)button.Template.FindName("Label", button);
            Assert.Equal(1, icon.Opacity);
            button.Press(true);
            var offset = Assert.IsType<TranslateTransform>(label.RenderTransform);
            Assert.Equal(1, offset.X);
            Assert.Equal(1, offset.Y);
            Assert.Contains(icon, Descendants(label));
            Assert.Contains(Descendants(label), element => element is AccessText);
            Assert.Equal(Color.FromRgb(128, 128, 128),
                ((SolidColorBrush)((Border)button.Template.FindName("Bevel", button)).BorderBrush).Color);
            button.Press(false);
            Assert.True(label.RenderTransform.Value.IsIdentity);
            button.IsEnabled = false;
            Assert.Equal(0.35, icon.Opacity);
            Assert.Equal(Color.FromRgb(128, 128, 128), ((SolidColorBrush)button.Foreground).Color);
            button.IsEnabled = true;
            Assert.Equal(1, icon.Opacity);
            var focus = Assert.Single(button.Template.Triggers.OfType<Trigger>(),
                trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty);
            var setter = Assert.Single(focus.Setters.OfType<Setter>());
            Assert.Equal("Focus", setter.TargetName);
            Assert.Equal(Visibility.Visible, setter.Value);
            window.ApplyTemplate();
            foreach (var name in new[] { "SystemMenuButton", "MinimizeButton", "MaximizeButton" })
            {
                var chrome = (Button)window.Template.FindName(name, window);
                chrome.ApplyTemplate();
                Assert.Null(ButtonIcon.GetSource(chrome));
                Assert.Null(chrome.ContentTemplate);
                Assert.Empty(Descendants(chrome).OfType<DecorativeIcon>());
            }
        }
        finally { window.Close(); }
    });

    private static void AssertAction(RetroWindow window, Button button)
    {
        var text = Assert.IsType<string>(button.Content);
        var key = text switch { "_Connect" => "ConnectIcon", "_Settings..." => "SettingsIcon",
            "_Help" => "HelpIcon", "_SEND" => "SendIcon", "S_top" => "StopIcon",
            "_Browse..." => "BrowseIcon", "_OK" => "ConfirmIcon", "_Cancel" => "CancelIcon",
            _ => throw new InvalidOperationException("Unexpected scoped action: " + text) };
        Assert.Same(window.Resources[key], ButtonIcon.GetSource(button));
        if (window is MainWindow)
        {
            Assert.Same(window, button.CommandTarget);
            Assert.Same(text switch { "_Connect" => ShellCommands.Connect,
                "_Settings..." => ShellCommands.ConfigureBackend, "_Help" => ShellCommands.About,
                "_SEND" => ShellCommands.Send, _ => ShellCommands.Stop }, button.Command);
        }
        else if (window is AboutWindow)
        {
            Assert.Same(SystemCommands.CloseWindowCommand, button.Command);
            Assert.True(button.IsDefault);
            Assert.True(button.IsCancel);
        }
        else
        {
            Assert.Null(button.Command);
            Assert.Equal(text == "_OK", button.IsDefault);
            Assert.Equal(text == "_Cancel" || window is ErrorWindow, button.IsCancel);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class PressableButton : Button
    {
        public void Press(bool pressed) => IsPressed = pressed;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Button icon/state/layout check timed out.");
        Assert.Null(failure);
    }
}
