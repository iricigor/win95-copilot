using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WinChatCopilot.Backend;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class RoleColorTests
{
    [Theory]
    [InlineData(false, "User")]
    [InlineData(true, "User")]
    [InlineData(false, "Assistant")]
    [InlineData(true, "Assistant")]
    [InlineData(true, "\u6f22 synthetic reader")]
    public void OnlyNamesUseExactRoleBrushesWithoutChangingTextOrWeights(bool timestamps, string userName) => OnSta(() =>
    {
        var view = new TranscriptView(() => false);
        var turns = new[]
        {
            new ChatTurn("multiline\nprompt \u6f22") { Response = "reply \u20ac", State = ResponseState.Completed },
            new ChatTurn("failed") { Response = "partial", State = ResponseState.Failed },
            new ChatTurn("cancelled") { State = ResponseState.Cancelled },
            new ChatTurn("waiting")
        };
        view.Update(turns, timestamps, 5, userName);
        AssertColors(view, false);
        Assert.EndsWith("Assistant: .....\r\n", view.Text);
        foreach (var paragraph in view.Document.Blocks.Cast<Paragraph>())
        {
            var runs = paragraph.Inlines.Cast<Run>().ToArray();
            Assert.Equal(FontWeights.Normal, runs[0].FontWeight);
            Assert.Equal(FontWeights.Bold, runs[1].FontWeight);
            Assert.Equal(FontWeights.Bold, runs[2].FontWeight);
            Assert.Equal(":", runs[2].Text);
            Assert.Equal(FontWeights.Normal, runs[3].FontWeight);
            Assert.Equal(FontWeights.Normal, runs[4].FontWeight);
        }
        view.UpdateWaitingIndicator(0);
        Assert.Equal(TranscriptDisplay.Format(turns, timestamps, userName: userName), view.Text);
        AssertColors(view, false);
        turns[^1].Response = "first text";
        view.Update(turns, timestamps, userName: userName);
        Assert.Equal(TranscriptDisplay.Format(turns, timestamps, userName: userName), view.Text);
        AssertColors(view, false);
        view.Update([], timestamps);
        view.Update([new ChatTurn("new")], timestamps, userName: userName);
        AssertColors(view, false);
    });

    [Fact]
    public void SharedNameBrushesMeetNormalTextContrastAgainstWhite() => OnSta(() =>
    {
        var view = new TranscriptView(() => false);
        foreach (var key in new[] { "UserNameBrush", "AssistantNameBrush" })
        {
            var color = Assert.IsType<SolidColorBrush>(view.FindResource(key)).Color;
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            var luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            Assert.Equal(byte.MaxValue, color.A);
            Assert.True(1.05 / (luminance + 0.05) >= 4.5, $"{key} does not meet 4.5:1 contrast.");
        }
    });

    [Fact]
    public void HighContrastChangesExistingRunsToLiveSystemBrushesAndPreservesSelection() => OnSta(() =>
    {
        var highContrast = false;
        var view = new TranscriptView(() => highContrast);
        var window = new Window { Content = view };
        try
        {
            window.Show();
            view.Update([new ChatTurn("synthetic selection") { Response = "reply", State = ResponseState.Completed }],
                true, userName: "Assistant");
            var paragraph = (Paragraph)view.Document.Blocks.FirstBlock;
            var body = (Run)paragraph.Inlines.LastInline;
            var start = body.ContentStart.GetPositionAtOffset(1)!;
            view.Selection.Select(start, start.GetPositionAtOffset(9)!);
            var selected = view.Selection.Text;
            var text = view.Text;
            foreach (var enabled in new[] { true, false, true })
            {
                highContrast = enabled;
                view.RefreshColors();
                AssertColors(view, enabled);
                Assert.Equal(enabled ? SystemColors.WindowBrush.Color : Colors.White,
                    Assert.IsType<SolidColorBrush>(view.Background).Color);
                Assert.Same(paragraph, view.Document.Blocks.FirstBlock);
                Assert.Equal(selected, view.Selection.Text);
                Assert.Equal(text, view.Text);
            }
            window.Content = null;
            highContrast = false;
            window.Content = view;
            view.UpdateLayout();
            view.RefreshColors();
            AssertColors(view, false);
            Assert.Equal(selected, view.Selection.Text);
        }
        finally { window.Close(); }
    });

    internal static void AssertColors(TranscriptView view, bool highContrast)
    {
        var index = 0;
        foreach (var paragraph in view.Document.Blocks.Cast<Paragraph>())
        {
            var runs = paragraph.Inlines.Cast<Run>().ToArray();
            Assert.Equal(5, runs.Length);
            var foreground = highContrast ? SystemColors.WindowTextBrush.Color : Colors.Black;
            var name = highContrast ? foreground : index % 2 == 0 ? Color.FromRgb(0, 0, 128) : Color.FromRgb(0, 128, 128);
            Assert.Equal(name, Assert.IsType<SolidColorBrush>(runs[1].Foreground).Color);
            if (!highContrast)
                Assert.Same(view.FindResource(index % 2 == 0 ? "UserNameBrush" : "AssistantNameBrush"), runs[1].Foreground);
            foreach (var run in new[] { runs[0], runs[2], runs[3], runs[4] })
                Assert.Equal(foreground, Assert.IsType<SolidColorBrush>(run.Foreground).Color);
            index++;
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Role-color test timed out.");
        Assert.Null(failure);
    }
}
