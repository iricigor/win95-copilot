using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class InputTests
{
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
            var transcript = (TextBox)window.FindName("Transcript");
            Assert.False(input.AcceptsReturn);
            ShellCommands.WordWrap.Execute(null, window);
            Assert.Equal(TextWrapping.Wrap, input.TextWrapping);
            Assert.Equal(TextWrapping.Wrap, transcript.TextWrapping);
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
    [InlineData(ResponseState.Completed, "\r\n\r\n")]
    [InlineData(ResponseState.Failed, "\r\n[Failed]\r\n\r\n")]
    [InlineData(ResponseState.Cancelled, "\r\n[Cancelled]\r\n\r\n")]
    public void TimestampFormattingKeepsMultilinePartialTextAndState(ResponseState state, string ending)
    {
        var turn = new ChatTurn("line one\n\u6f22") { Response = "partial\r\ntext", State = state };
        Assert.Equal("User:\r\nline one\n\u6f22\r\n\r\nAssistant:\r\npartial\r\ntext" + ending,
            TranscriptDisplay.Format([turn], false));
        Assert.Equal(TranscriptDisplay.Heading("User", turn.SubmittedAt, true) + turn.Prompt + "\r\n\r\n" +
            TranscriptDisplay.Heading("Assistant", turn.SubmittedAt, true) + turn.Response + ending,
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
            var transcript = (TextBox)window.FindName("Transcript");
            input.Text = "synthetic scroll";
            var run = window.SendAsync();
            await WaitAsync(() => transcript.Text.Contains("synthetic line 149") &&
                transcript.VerticalOffset > 0 && transcript.VerticalOffset >= transcript.ExtentHeight - transcript.ViewportHeight - 1);
            transcript.ScrollToVerticalOffset(100);
            await WaitAsync(() => Math.Abs(transcript.VerticalOffset - 100) < 1);
            var before = transcript.VerticalOffset;
            transcript.Select(3, 6);
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
            Assert.Equal(3, transcript.SelectionStart);
            Assert.Equal(6, transcript.SelectionLength);
            Assert.Contains("synthetic line 169", transcript.Text);
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
            var transcript = (TextBox)window.FindName("Transcript");
            input.Text = "synthetic input";
            transcript.Text = "synthetic transcript";
            Assert.Same(ApplicationCommands.Copy, ShellCommands.Copy);
            Assert.Same(ApplicationCommands.Paste, ShellCommands.Paste);
            Assert.Same(ApplicationCommands.SelectAll, ShellCommands.SelectAll);
            foreach (var area in new[] { input, transcript })
            {
                FocusManager.SetFocusedElement(window, area);
                Assert.True(ShellCommands.SelectAll.CanExecute(null, area));
                ShellCommands.SelectAll.Execute(null, area);
                Assert.Equal(area.Text, area.SelectedText);
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
