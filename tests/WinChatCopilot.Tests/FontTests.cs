using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class FontTests
{
    [Fact]
    public void CompatibleSettingsRoundTripEveryFontAndPreserveUnrelatedPreferences()
    {
        using var fixture = new Fixture();
        Assert.Equal("Courier New", fixture.Store.Load().TextFont);
        File.WriteAllText(fixture.Store.FilePath, "{\"SchemaVersion\":1,\"WordWrap\":true}");
        Assert.Equal("Courier New", fixture.Store.Load().TextFont);
        foreach (var font in TranscriptFont.Choices)
        {
            var settings = new AppSettings { TextFont = font, UserName = "synthetic reader" };
            foreach (var changed in new[]
            {
                settings, settings with { WordWrap = true }, settings with { ShowTimestamp = true },
                settings with { AutoConnect = true }, settings with { SendMode = SendMode.Enter },
                settings with { ExecutablePath = fixture.Executable }, settings with { UserName = "another reader" }
            })
            {
                fixture.Store.Save(changed);
                Assert.Equal(changed, fixture.Store.Load());
                Assert.Equal(font, fixture.Store.Load().TextFont);
            }
        }
    }

    [Theory]
    [InlineData("{\"TextFont\":null}")]
    [InlineData("{\"TextFont\":\"Arial\"}")]
    [InlineData("{\"TextFont\":\"courier new\"}")]
    [InlineData("{\"TextFont\":\"\"}")]
    public void InvalidStoredFontIsActionableAndNeverRewritten(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.FilePath, json);
        Assert.Contains("Options > Font", Assert.Throws<IOException>(() => fixture.Store.Load()).Message);
        Assert.Equal(json, File.ReadAllText(fixture.Store.FilePath));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(new AppSettings { TextFont = "Arial" }));
        Assert.Equal(json, File.ReadAllText(fixture.Store.FilePath));
    }

    [Fact]
    public void OptionsSelectInstalledFontsImmediatelyAndRestoreOnRestart() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            Assert.False(ShellCommands.SelectFont.CanExecute("Courier New", window));
            await window.InitializeBackendAsync();
            window.Show();
            var view = (TranscriptView)window.FindName("Transcript");
            var input = (TextBox)window.FindName("PromptInput");
            input.Text = "unchanged synthetic input";
            input.Select(2, 5);
            input.Focus();
            var turn = new ChatTurn("multiline\n\u6f22") { Response = "reply \u20ac", State = ResponseState.Completed };
            view.Update([turn], false);
            var text = view.Text;
            var items = ((MenuItem)window.FindName("FontMenu")).Items.Cast<MenuItem>().ToArray();
            Assert.Equal(TranscriptFont.Choices, items.Select(item => (string)item.CommandParameter));
            foreach (var font in TranscriptFont.Choices.Where(TranscriptFont.IsInstalled))
            {
                var paragraph = view.Document.Blocks.FirstBlock;
                ShellCommands.SelectFont.Execute(font, window);
                Assert.Equal(font, input.FontFamily.Source);
                Assert.Equal(font, view.FontFamily.Source);
                Assert.Equal(font, view.Document.FontFamily.Source);
                Assert.Equal(font, ((TextBlock)items.Single(item => (string)item.CommandParameter == font).Header).FontFamily.Source);
                Assert.Single(items, item => item.IsChecked);
                Assert.Equal(font, (string)items.Single(item => item.IsChecked).CommandParameter);
                Assert.Same(paragraph, view.Document.Blocks.FirstBlock);
                Assert.Equal(text, view.Text);
                Assert.Equal("unchanged synthetic input", input.Text);
                Assert.Equal("chang", input.SelectedText);
                Assert.True(input.IsKeyboardFocusWithin);
                Assert.Equal("Microsoft Sans Serif", window.FontFamily.Source);
                Assert.Equal(font, fixture.Store.Load().TextFont);
                ShellCommands.WordWrap.Execute(null, window);
                ShellCommands.ShowTimestamp.Execute(null, window);
                ShellCommands.SendWithEnter.Execute(null, window);
                ShellCommands.AutoConnect.Execute(null, window);
                window.SaveUserName("synthetic reader");
                backend.Save(backend.Settings with { ExecutablePath = fixture.Executable, AutoConnect = false });
                Assert.Equal(font, fixture.Store.Load().TextFont);
                var restarted = new MainWindow(fixture.Controller());
                try
                {
                    await restarted.InitializeBackendAsync();
                    Assert.Equal(font, ((TextBox)restarted.FindName("PromptInput")).FontFamily.Source);
                    Assert.Equal(font, ((TranscriptView)restarted.FindName("Transcript")).Document.FontFamily.Source);
                    Assert.Single(((MenuItem)restarted.FindName("FontMenu")).Items.Cast<MenuItem>(), item => item.IsChecked);
                }
                finally { restarted.Close(); }
                view.Update([turn], false);
            }
            Assert.False(ShellCommands.SelectFont.CanExecute("Arial", window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UnavailableSavedChoiceIsDisabledWarnedAndNotSilentlyReplaced() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.Save(new AppSettings { ExecutablePath = fixture.Executable, TextFont = "Lucida Console" });
        var backend = fixture.Controller();
        var window = new MainWindow(backend, _ => null, font => font != "Lucida Console");
        try
        {
            await window.InitializeBackendAsync();
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var items = ((MenuItem)window.FindName("FontMenu")).Items.Cast<MenuItem>().ToArray();
            var missing = items.Single(item => (string)item.CommandParameter == "Lucida Console");
            Assert.False(missing.IsEnabled);
            Assert.Contains("not installed", ((TextBlock)missing.Header).Text);
            Assert.Contains("another available font", (string)missing.ToolTip);
            Assert.False(ShellCommands.SelectFont.CanExecute("Lucida Console", window));
            Assert.DoesNotContain(items, item => item.IsChecked);
            Assert.Equal("Courier New", ((TranscriptView)window.FindName("Transcript")).FontFamily.Source);
            Assert.Equal("Courier New", ((TextBox)window.FindName("PromptInput")).FontFamily.Source);
            var warning = (TextBlock)window.FindName("FontStatus");
            Assert.Equal(Visibility.Visible, warning.Visibility);
            Assert.Contains("Saved font 'Lucida Console'", warning.Text);
            Assert.Contains("Temporarily rendering with Courier New", warning.Text);
            ShellCommands.WordWrap.Execute(null, window);
            Assert.Equal("Lucida Console", fixture.Store.Load().TextFont);
            ShellCommands.SelectFont.Execute("Microsoft Sans Serif", window);
            Assert.Equal("Microsoft Sans Serif", fixture.Store.Load().TextFont);
            Assert.Equal(Visibility.Collapsed, warning.Visibility);
            Assert.Single(items, item => item.IsChecked);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LockedSaveShowsErrorAndKeepsDisplayedAndPersistedFont() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var backend = fixture.Controller();
        var window = new MainWindow(backend, _ => null, _ => true);
        var observed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var error = window.OwnedWindows.OfType<ErrorWindow>().SingleOrDefault();
            if (error is null) return;
            timer.Stop();
            observed = true;
            var panel = (StackPanel)error.Content;
            Assert.Contains("Cannot save settings", ((TextBlock)((ScrollViewer)panel.Children[0]).Content).Text);
            error.Close();
        };
        try
        {
            await window.InitializeBackendAsync();
            window.Show();
            var previous = backend.Settings;
            using (File.Open(fixture.Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                timer.Start();
                ShellCommands.SelectFont.Execute("Lucida Console", window);
            }
            Assert.True(observed);
            Assert.Equal(previous, backend.Settings);
            Assert.Equal(previous, fixture.Store.Load());
            Assert.Equal("Courier New", ((TranscriptView)window.FindName("Transcript")).FontFamily.Source);
            Assert.Equal("Courier New", ((TextBox)window.FindName("PromptInput")).FontFamily.Source);
            Assert.Equal("Courier New", (string)((MenuItem)window.FindName("FontMenu")).Items.Cast<MenuItem>().Single(item => item.IsChecked).CommandParameter);
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Fact]
    public void EachFontMeasuresMultilineUnicodeAndBoldHeadingsWithRealScrollingAndWrapping() => OnSta(() =>
    {
        var view = new TranscriptView();
        var window = new Window { Content = view, Width = 360, Height = 240 };
        try
        {
            window.Show();
            var turn = new ChatTurn("Wi \u6f22 \u20ac\n" + new string('W', 180))
                { Response = "reply\nsecond line  ", State = ResponseState.Completed };
            foreach (var font in TranscriptFont.Choices)
            {
                view.SetTextFont(new FontFamily(font));
                view.SetWordWrap(false);
                view.Update([turn], true, userName: new string('W', 180));
                view.UpdateLayout();
                var width = 0.0;
                foreach (var paragraph in view.Document.Blocks.Cast<Paragraph>())
                {
                    var runs = paragraph.Inlines.Cast<Run>().ToArray();
                    var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture, view.FlowDirection,
                        new Typeface(font), view.FontSize, view.Foreground, VisualTreeHelper.GetDpi(view).PixelsPerDip);
                    formatted.SetFontWeight(FontWeights.Bold, runs[0].Text.Length, runs[1].Text.Length + 1);
                    width = Math.Max(width, formatted.WidthIncludingTrailingWhitespace);
                }
                Assert.InRange(view.Document.PageWidth - width, 0, 2);
                Assert.True(view.ExtentWidth > view.ViewportWidth);
                view.ScrollToHorizontalOffset(100);
                view.UpdateLayout();
                Assert.True(view.HorizontalOffset > 0);
                var textBefore = view.Text;
                var heightBefore = view.ExtentHeight;
                view.SetWordWrap(true);
                view.UpdateLayout();
                Assert.True(double.IsNaN(view.Document.PageWidth));
                Assert.Equal(ScrollBarVisibility.Disabled, view.HorizontalScrollBarVisibility);
                Assert.InRange(view.HorizontalOffset, 0, 1);
                Assert.True(view.ExtentHeight > heightBefore);
                Assert.Equal(textBefore, view.Text);
            }
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FontChangesDuringDelayedStreamPreserveSelectionFocusScrollAndSession(bool followAgain, bool timestamps) => OnSta(async () =>
    {
        using var fixture = new Fixture("long-stream");
        var backend = fixture.Controller();
        fixture.Store.Save(fixture.Store.Load() with { ShowTimestamp = timestamps, UserName = "Assistant" });
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            window.Show();
            var input = (TextBox)window.FindName("PromptInput");
            var view = (TranscriptView)window.FindName("Transcript");
            input.Text = "synthetic font prompt \u6f22\nmultiline";
            var generation = window.SendAsync();
            await WaitAsync(() => view.Text.Contains("synthetic line 149"));
            view.ScrollToVerticalOffset(100);
            await WaitAsync(() => Math.Abs(view.VerticalOffset - 100) < 1);
            var body = (Run)((Paragraph)view.Document.Blocks.FirstBlock).Inlines.LastInline;
            var start = body.ContentStart.GetPositionAtOffset(1)!;
            view.Selection.Select(start, start.GetPositionAtOffset(5)!);
            input.Focus();
            var selected = view.Selection.Text;
            var offset = view.VerticalOffset;
            var session = backend.Session;
            var canonical = view.Text;
            foreach (var font in TranscriptFont.Choices.Where(TranscriptFont.IsInstalled))
            {
                ShellCommands.SelectFont.Execute(font, window);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(selected, view.Selection.Text);
                Assert.True(input.IsKeyboardFocusWithin);
                Assert.InRange(Math.Abs(view.VerticalOffset - offset), 0, 1);
                Assert.Equal(canonical, view.Text);
                Assert.Same(session, backend.Session);
                RoleColorTests.AssertColors(view, SystemParameters.HighContrast);
            }
            if (followAgain)
            {
                view.ScrollToEnd();
                await WaitAsync(() => view.VerticalOffset >= view.ExtentHeight - view.ViewportHeight - 1);
            }
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            await generation;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(selected, view.Selection.Text);
            Assert.True(input.IsKeyboardFocusWithin);
            if (followAgain) Assert.InRange(view.ExtentHeight - view.ViewportHeight - view.VerticalOffset, 0, 1);
            else Assert.InRange(Math.Abs(view.VerticalOffset - offset), 0, 1);
            Assert.Equal(TranscriptDisplay.Format(window.Turns, timestamps, userName: "Assistant"), view.Text);
            RoleColorTests.AssertColors(view, SystemParameters.HighContrast);
            var request = Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
            Assert.Equal("synthetic font prompt \u6f22\nmultiline", JsonSerializer.Deserialize<string[]>(request)![^1]);
            Assert.Same(session, backend.Session);
        }
        finally { await window.StopAsync(); window.Close(); }
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Font readiness gate timed out.");
            await Task.Delay(20);
        }
    }

    private static void OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Font test timed out.");
        Assert.Null(failure);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "winchat-font-" + Guid.NewGuid());
        public string Work => Path.Combine(Root, "work");
        public string Executable => Path.Combine(AppContext.BaseDirectory, "Fake CLI", "WinChatCopilot.FakeCli.exe");
        public SettingsStore Store { get; }
        public Fixture(string scenario = "complete")
        {
            Directory.CreateDirectory(Work);
            File.WriteAllText(Path.Combine(Work, "chat-scenario"), scenario);
            Store = new SettingsStore(Path.Combine(Root, "settings.json"));
        }
        public BackendController Controller()
        {
            if (!File.Exists(Store.FilePath)) Store.Save(new AppSettings { ExecutablePath = Executable });
            return new BackendController(Store, new BackendDiscovery("", []), new CopilotReadiness(Root).PrepareAsync);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
