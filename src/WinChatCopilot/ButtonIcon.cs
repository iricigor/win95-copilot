using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinChatCopilot;

public static class ButtonIcon
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(ImageSource), typeof(ButtonIcon), new PropertyMetadata(null));

    public static ImageSource? GetSource(DependencyObject element) =>
        (ImageSource?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, ImageSource? value) =>
        element.SetValue(SourceProperty, value);
}

public sealed class DecorativeIcon : Image
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
