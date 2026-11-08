using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class InputTests
{
    [Theory]
    [InlineData(0, 0, 0, "en-US", "[00:00] ")]
    [InlineData(9, 5, 330, "fr-FR", "[09:05] ")]
    [InlineData(23, 59, -480, "ar-SA", "[23:59] ")]
    [InlineData(9, 5, -210, "fi-FI", "[09:05] ")]
    [InlineData(23, 59, 840, "ja-JP", "[23:59] ")]
    public void CompactHeadingsUseInvariantSubmittedTimeWithoutOffsetConversion(
        int hour, int minute, int offsetMinutes, string culture, string stamp)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var submitted = new DateTimeOffset(2026, 11, 8, hour, minute, 47, TimeSpan.FromMinutes(offsetMinutes));
            var turn = new ChatTurn("prompt", submitted) { Response = "reply", State = ResponseState.Completed };
            foreach (var timestamps in new[] { false, true })
            {
                var prefix = timestamps ? stamp : "";
                Assert.Equal(prefix + "User: ", TranscriptDisplay.Heading("User", submitted, timestamps));
                Assert.Equal(prefix + "Assistant: ", TranscriptDisplay.Heading("Assistant", submitted, timestamps));
                Assert.Equal(prefix + "User: prompt\r\n" + prefix + "Assistant: reply\r\n",
                    TranscriptDisplay.Format([turn], timestamps, markGenerating: true));
            }
            Assert.Equal(submitted, turn.SubmittedAt);
            Assert.Null(turn.FinishedAt);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(SendMode.ControlEnter, ModifierKeys.None, false)]
    [InlineData(SendMode.ControlEnter, ModifierKeys.Shift, false)]
    [InlineData(SendMode.ControlEnter, ModifierKeys.Control, true)]
    [InlineData(SendMode.ControlEnter, ModifierKeys.Control | ModifierKeys.Shift, false)]
    [InlineData(SendMode.Enter, ModifierKeys.None, true)]
    [InlineData(SendMode.Enter, ModifierKeys.Shift, true)]
    [InlineData(SendMode.Enter, ModifierKeys.Control, true)]
    public void ShortcutModesShareSubmissionAndBusyWhitespaceUnavailableGuards(
        SendMode mode, ModifierKeys modifiers, bool sends) => OnSta(async () =>
    {
        using var fixture = new Fixture("stream");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            backend.Save(backend.Settings with { SendMode = mode });
            var input = (TextBox)window.FindName("PromptInput");
            input.Text = "synthetic\nmultiline";
            Assert.Equal(sends, window.HandlePromptKey(Key.Enter, modifiers));
            Assert.Empty(window.Turns);
            await backend.ConnectAsync(CancellationToken.None);
            input.Text = " \r\n\t";
            Assert.Equal(sends, window.HandlePromptKey(Key.Enter, modifiers));
            await window.SendAsync();
            Assert.Empty(window.Turns);
            input.Text = "synthetic\nmultiline";
            Assert.False(window.HandlePromptKey(Key.A, modifiers));
            Assert.Equal(sends, window.HandlePromptKey(Key.Enter, modifiers));
            if (!sends)
            {
                Assert.Empty(window.Turns);
                Assert.True(input.AcceptsReturn);
                ShellCommands.Send.Execute(null, window);
            }
            await WaitAsync(() => window.Turns.Count == 1 && window.Turns[0].Response.Length > 0);
            window.HandlePromptKey(Key.Enter, modifiers);
            ShellCommands.Send.Execute(null, window);
            await window.SendAsync();
            Assert.Single(window.Turns);
            Assert.True(input.IsReadOnly);
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            await WaitAsync(() => window.Turns[0].State == ResponseState.Completed);
            Assert.Empty(input.Text);
            var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(fixture.Work, "chat.arguments.json")))!;
            Assert.Equal("synthetic\nmultiline", args[^1]);
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PreferencesPersistAndTimestampReformattingPreservesCanonicalDataDuringStreaming() => OnSta(async () =>
    {
        using var fixture = new Fixture("stream");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            Assert.True(((MenuItem)window.FindName("SendControlEnterMenu")).IsChecked);
            ShellCommands.SendWithEnter.Execute(null, window);
            ShellCommands.SendWithEnter.Execute(null, window);
            Assert.True(((MenuItem)window.FindName("SendEnterMenu")).IsChecked);
            Assert.False(((MenuItem)window.FindName("SendControlEnterMenu")).IsChecked);
            var input = (TextBox)window.FindName("PromptInput");
            var transcript = (TranscriptView)window.FindName("Transcript");
            Assert.False(input.AcceptsReturn);
            ShellCommands.WordWrap.Execute(null, window);
            Assert.Equal(TextWrapping.Wrap, input.TextWrapping);
            Assert.True(double.IsNaN(transcript.Document.PageWidth));
            Assert.Equal(ScrollBarVisibility.Disabled, transcript.HorizontalScrollBarVisibility);
            await backend.ConnectAsync(CancellationToken.None);
            input.Text = "canonical \u6f22\nmultiline";
            var run = window.SendAsync();
            await WaitAsync(() => window.Turns[0].Response == "first ");
            var turn = window.Turns[0];
            var original = transcript.Text;
            ShellCommands.ShowTimestamp.Execute(null, window);
            Assert.Contains(TranscriptDisplay.Heading("User", turn.SubmittedAt, true), transcript.Text);
            Assert.Contains(TranscriptDisplay.Heading("Assistant", turn.SubmittedAt, true), transcript.Text);
            ShellCommands.ShowTimestamp.Execute(null, window);
            Assert.Equal(original, transcript.Text);
            Assert.Equal("canonical \u6f22\nmultiline", turn.Prompt);
            Assert.Equal("first ", turn.Response);
            Assert.Null(turn.FinishedAt);
            ShellCommands.ShowTimestamp.Execute(null, window);
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            await run;
            Assert.Equal(TranscriptDisplay.Format(window.Turns, true), transcript.Text);
            Assert.NotNull(turn.FinishedAt);
            var loaded = fixture.Controller();
            loaded.Load();
            Assert.Equal(SendMode.Enter, loaded.Settings.SendMode);
            Assert.True(loaded.Settings.WordWrap);
            Assert.True(loaded.Settings.ShowTimestamp);
            var restarted = new MainWindow(loaded);
            try
            {
                await restarted.InitializeBackendAsync();
                Assert.True(((MenuItem)restarted.FindName("SendEnterMenu")).IsChecked);
                Assert.True(((MenuItem)restarted.FindName("WordWrapMenu")).IsChecked);
                Assert.True(((MenuItem)restarted.FindName("TimestampMenu")).IsChecked);
                Assert.False(((TextBox)restarted.FindName("PromptInput")).AcceptsReturn);
            }
            finally { restarted.Close(); }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(ResponseState.Generating, "")]
    [InlineData(ResponseState.Completed, "")]
    [InlineData(ResponseState.Failed, "\r\n[Failed]")]
    [InlineData(ResponseState.Cancelled, "\r\n[Cancelled]")]
    public void TimestampFormattingKeepsMultilinePartialTextAndState(ResponseState state, string ending)
    {
        var turn = new ChatTurn("line one\n\u6f22") { Response = "partial\r\ntext", State = state };
        Assert.Equal("User: line one\n\u6f22\r\nAssistant: partial\r\ntext" + ending + "\r\n",
            TranscriptDisplay.Format([turn], false));
        Assert.Equal(TranscriptDisplay.Heading("User", turn.SubmittedAt, true) + turn.Prompt + "\r\n" +
            TranscriptDisplay.Heading("Assistant", turn.SubmittedAt, true) + turn.Response + ending + "\r\n",
            TranscriptDisplay.Format([turn], true));
        Assert.Empty(TranscriptDisplay.Format([], true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedStreamRespectsScrollAwayAndResumesOnlyAtBottom(bool returnToBottom) => OnSta(async () =>
    {
        using var fixture = new Fixture("long-stream");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            window.Show();
            var input = (TextBox)window.FindName("PromptInput");
            var transcript = (TranscriptView)window.FindName("Transcript");
            input.Text = "synthetic scroll";
            var run = window.SendAsync();
            await WaitAsync(() => transcript.Text.Contains("synthetic line 149") &&
                transcript.VerticalOffset > 0 && transcript.VerticalOffset >= transcript.ExtentHeight - transcript.ViewportHeight - 1);
            transcript.ScrollToVerticalOffset(100);
            await WaitAsync(() => Math.Abs(transcript.VerticalOffset - 100) < 1);
            var before = transcript.VerticalOffset;
            var promptBody = (Run)((Paragraph)transcript.Document.Blocks.FirstBlock).Inlines.LastInline;
            var selectionStart = promptBody.ContentStart.GetPositionAtOffset(1)!;
            var selectionEnd = selectionStart.GetPositionAtOffset(6)!;
            transcript.Selection.Select(selectionStart, selectionEnd);
            var selectedText = transcript.Selection.Text;
            var textBeforeSelection = new TextRange(transcript.Document.ContentStart, transcript.Selection.Start).Text;
            input.Focus();
            var session = backend.Session;
            var turn = window.Turns[0];
            var response = turn.Response;
            var submittedAt = turn.SubmittedAt;
            ShellCommands.ShowTimestamp.Execute(null, window);
            Assert.Equal(selectedText, transcript.Selection.Text);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.InRange(Math.Abs(transcript.VerticalOffset - before), 0, 1);
            ShellCommands.ShowTimestamp.Execute(null, window);
            Assert.Equal(selectedText, transcript.Selection.Text);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.InRange(Math.Abs(transcript.VerticalOffset - before), 0, 1);
            Assert.Equal("synthetic scroll", input.Text);
            Assert.Equal("synthetic scroll", turn.Prompt);
            Assert.Equal(response, turn.Response);
            Assert.Equal(submittedAt, turn.SubmittedAt);
            Assert.Equal(ResponseState.Generating, turn.State);
            Assert.Same(session, backend.Session);
            if (returnToBottom)
            {
                transcript.ScrollToEnd();
                await WaitAsync(() => transcript.VerticalOffset >= transcript.ExtentHeight - transcript.ViewportHeight - 1);
            }
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            await run;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (returnToBottom)
                Assert.InRange(transcript.ExtentHeight - transcript.ViewportHeight - transcript.VerticalOffset, 0, 1);
            else Assert.InRange(Math.Abs(transcript.VerticalOffset - before), 0, 1);
            Assert.Equal(selectedText, transcript.Selection.Text);
            Assert.Equal(textBeforeSelection, new TextRange(transcript.Document.ContentStart, transcript.Selection.Start).Text);
            Assert.Contains("synthetic line 169", transcript.Text);
            Assert.Same(session, backend.Session);
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NativeEditCommandsRetainFocusScopeAndRespectReadOnlyTranscript() => OnSta(async () =>
    {
        using var fixture = new Fixture("complete");
        var window = new MainWindow(fixture.Controller());
        try
        {
            await window.InitializeBackendAsync();
            var input = (TextBox)window.FindName("PromptInput");
            var transcript = (TranscriptView)window.FindName("Transcript");
            input.Text = "synthetic input";
            transcript.Document.Blocks.Add(new Paragraph(new Run("synthetic transcript")));
            Assert.Same(ApplicationCommands.Copy, ShellCommands.Copy);
            Assert.Same(ApplicationCommands.Paste, ShellCommands.Paste);
            Assert.Same(ApplicationCommands.SelectAll, ShellCommands.SelectAll);
            foreach (var area in new TextBoxBase[] { input, transcript })
            {
                FocusManager.SetFocusedElement(window, area);
                Assert.True(ShellCommands.SelectAll.CanExecute(null, area));
                ShellCommands.SelectAll.Execute(null, area);
                Assert.Equal(area == input ? input.Text : transcript.Text,
                    area == input ? input.SelectedText : transcript.Selection.Text);
                Assert.True(ShellCommands.Copy.CanExecute(null, area));
            }
            Assert.False(ShellCommands.Paste.CanExecute(null, transcript));
            Assert.False(ApplicationCommands.Cut.CanExecute(null, transcript));
            input.IsReadOnly = true;
            Assert.False(ShellCommands.Paste.CanExecute(null, input));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FailedPreferenceWriteReportsErrorAndKeepsPreviousChecksAndText() => OnSta(async () =>
    {
        using var fixture = new Fixture("complete");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        var errorSeen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (var error in window.OwnedWindows.OfType<ErrorWindow>().ToArray())
            {
                errorSeen = true;
                error.Close();
            }
        };
        try
        {
            await window.InitializeBackendAsync();
            var input = (TextBox)window.FindName("PromptInput");
            input.Text = "preserved synthetic input";
            window.Show();
            using var locked = File.Open(Path.Combine(fixture.Root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            timer.Start();
            ShellCommands.SendWithEnter.Execute(null, window);
            Assert.True(errorSeen);
            Assert.Equal(SendMode.ControlEnter, backend.Settings.SendMode);
            Assert.True(((MenuItem)window.FindName("SendControlEnterMenu")).IsChecked);
            Assert.False(((MenuItem)window.FindName("SendEnterMenu")).IsChecked);
            Assert.True(input.AcceptsReturn);
            Assert.Equal("preserved synthetic input", input.Text);
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Theory]
    [InlineData(false, ResponseState.Completed, "Hi!\r\n")]
    [InlineData(true, ResponseState.Completed, "Hi!\n")]
    [InlineData(false, ResponseState.Failed, "partial\r\n")]
    [InlineData(true, ResponseState.Cancelled, "partial\n\n")]
    [InlineData(false, ResponseState.Failed, "")]
    [InlineData(true, ResponseState.Cancelled, "\r\n")]
    [InlineData(false, ResponseState.Generating, "partial")]
    [InlineData(true, ResponseState.Generating, "partial")]
    public void RichTranscriptHasOnlyBoldLabelsAndCompactNormalBodies(
        bool timestamps, ResponseState state, string response) => OnSta(() =>
    {
        var view = new TranscriptView();
        var turns = new[]
        {
            new ChatTurn("Hi\r\n", new DateTimeOffset(2026, 11, 8, 9, 5, 47, TimeSpan.FromHours(-8)))
                { Response = response, State = state },
            new ChatTurn("line\t\u6f22\n\n  next \n") { Response = "reply\n\ninside\t \u20ac\r\n", State = ResponseState.Completed }
        };
        view.Update(turns, timestamps);
        Assert.Equal(TranscriptDisplay.Format(turns, timestamps), view.Text);
        Assert.Equal(4, view.Document.Blocks.Count);
        var paragraphIndex = 0;
        foreach (var paragraph in view.Document.Blocks.Cast<Paragraph>())
        {
            var turn = turns[paragraphIndex++ / 2];
            Assert.Equal(new Thickness(0), paragraph.Margin);
            Assert.Equal(new Thickness(0), paragraph.Padding);
            var runs = paragraph.Inlines.Cast<Run>().ToArray();
            Assert.Equal(5, runs.Length);
            Assert.Equal(FontWeights.Normal, runs[0].FontWeight);
            Assert.Equal(TranscriptDisplay.Timestamp(turn.SubmittedAt, timestamps), runs[0].Text);
            Assert.Equal(FontWeights.Bold, runs[1].FontWeight);
            Assert.Contains(runs[1].Text, new[] { "User", "Assistant" });
            Assert.Equal(FontWeights.Bold, runs[2].FontWeight);
            Assert.Equal(":", runs[2].Text);
            Assert.Equal(FontWeights.Normal, runs[3].FontWeight);
            Assert.Empty(runs[3].Text);
            Assert.Equal(FontWeights.Normal, runs[4].FontWeight);
            Assert.StartsWith(" ", runs[4].Text);
        }
        Assert.Equal("Hi\r\n", turns[0].Prompt);
        Assert.Equal(response, turns[0].Response);
        Assert.Equal("line\t\u6f22\n\n  next \n", turns[1].Prompt);
        Assert.Equal("reply\n\ninside\t \u20ac\r\n", turns[1].Response);
        return Task.CompletedTask;
    });

    [Fact]
    public void GreetingAndProgressiveBoundariesPreserveRunsSelectionAndCanonicalText() => OnSta(() =>
    {
        var view = new TranscriptView();
        var turn = new ChatTurn("Hi");
        view.Update([turn], false);
        Assert.Equal("User: Hi\r\nAssistant: \r\n", view.Text);
        var assistant = (Paragraph)view.Document.Blocks.LastBlock;
        var body = (Run)assistant.Inlines.LastInline;
        foreach (var chunk in new[] { "Hi!", "\r", "\n", "\n", "How", "\t\u6f22", "\r\n" })
        {
            turn.Response += chunk;
            view.Update([turn], false);
            Assert.Same(assistant, view.Document.Blocks.LastBlock);
            Assert.Same(body, assistant.Inlines.LastInline);
            Assert.Equal(TranscriptDisplay.Format([turn], false), view.Text);
        }
        var start = body.ContentStart.GetPositionAtOffset(1)!;
        var end = start.GetPositionAtOffset(3)!;
        view.Selection.Select(start, end);
        view.Update([turn], true);
        Assert.Equal("Hi!", view.Selection.Text);
        view.Update([turn], false);
        turn.State = ResponseState.Completed;
        view.Update([turn], false);
        Assert.Equal("Hi!", view.Selection.Text);
        Assert.Equal("Hi!\r\n\nHow\t\u6f22\r\n", turn.Response);
        turn.Response = "Hi! How can I help you today?";
        view.Update([turn], false);
        Assert.Equal("User: Hi\r\nAssistant: Hi! How can I help you today?\r\n", view.Text);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PopulatedRichTranscriptWrapCopyPasteAndFocusUseNativeBehavior(bool timestamps) => OnSta(async () =>
    {
        using var fixture = new Fixture("complete");
        var window = new MainWindow(fixture.Controller());
        try
        {
            await window.InitializeBackendAsync();
            window.Show();
            var view = (TranscriptView)window.FindName("Transcript");
            var turn = new ChatTurn(new string('W', 400))
            {
                Response = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"line {i}")),
                State = ResponseState.Completed
            };
            view.Update([turn], timestamps);
            view.SetWordWrap(false);
            view.UpdateLayout();
            foreach (var paragraph in view.Document.Blocks.Cast<Paragraph>())
            {
                var runs = paragraph.Inlines.Cast<Run>().ToArray();
                var text = string.Concat(runs.Select(run => run.Text)).Split('\n')[0];
                var measured = new FormattedText(text, CultureInfo.InvariantCulture, view.FlowDirection,
                    new Typeface(view.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    view.FontSize, view.Foreground, VisualTreeHelper.GetDpi(view).PixelsPerDip);
                measured.SetFontWeight(FontWeights.Bold, runs[0].Text.Length, runs[1].Text.Length + 1);
                Assert.True(view.Document.PageWidth >= measured.WidthIncludingTrailingWhitespace);
            }
            Assert.True(view.ExtentWidth > view.ViewportWidth);
            Assert.True(view.ExtentHeight > view.ViewportHeight);
            view.ScrollToHorizontalOffset(100);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.InRange(view.HorizontalOffset, 99, 101);
            view.Focus();
            ShellCommands.SelectAll.Execute(null, view);
            Assert.Equal(view.Text, view.Selection.Text);
            string? copied = null;
            DataObject.AddCopyingHandler(view, (_, e) =>
            {
                copied = (string)e.DataObject.GetData(DataFormats.UnicodeText);
                e.CancelCommand();
            });
            ShellCommands.Copy.Execute(null, view);
            Assert.Equal(TranscriptDisplay.Format([turn], timestamps), copied);
            Assert.DoesNotContain("**", copied!);
            Assert.False(ShellCommands.Paste.CanExecute(null, view));
            ShellCommands.Paste.Execute(null, view);
            Assert.Equal(copied, view.Text);
            var selected = view.Selection.Text;
            view.SetWordWrap(true);
            view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(double.IsNaN(view.Document.PageWidth));
            Assert.InRange(view.ExtentWidth - view.ViewportWidth, 0, 1);
            Assert.Equal(selected, view.Selection.Text);
            Assert.True(view.IsKeyboardFocusWithin);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ExtremeLineKeepsAllTextWithinSupportedNativeLayoutWidth() => OnSta(() =>
    {
        var view = new TranscriptView { FontSize = 14 };
        var turn = new ChatTurn("synthetic") { Response = new string('W', 130_000), State = ResponseState.Completed };
        view.Update([turn], false);
        Assert.InRange(view.Document.PageWidth, 1, 1_000_000);
        Assert.Equal(TranscriptDisplay.Format([turn], false), view.Text);
        Assert.Equal(130_000, turn.Response.Length);
        return Task.CompletedTask;
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Input/scroll readiness gate timed out.");
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Input/scroll test timed out.");
        Assert.Null(failure);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "winchat-input-" + Guid.NewGuid());
        public string Work => Path.Combine(Root, "work");
        public Fixture(string scenario)
        {
            Directory.CreateDirectory(Work);
            File.WriteAllText(Path.Combine(Work, "chat-scenario"), scenario);
        }
        public BackendController Controller()
        {
            var store = new SettingsStore(Path.Combine(Root, "settings.json"));
            if (!File.Exists(store.FilePath))
                store.Save(new AppSettings { ExecutablePath = Path.Combine(AppContext.BaseDirectory, "Fake CLI", "WinChatCopilot.FakeCli.exe") });
            return new BackendController(store, new BackendDiscovery("", []), new CopilotReadiness(Root).PrepareAsync);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
