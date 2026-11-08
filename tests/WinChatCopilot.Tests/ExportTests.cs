using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class ExportTests
{
    [Theory]
    [InlineData(ResponseState.Completed, "")]
    [InlineData(ResponseState.Failed, "\r\n[Failed]")]
    [InlineData(ResponseState.Cancelled, "\r\n[Cancelled]")]
    [InlineData(ResponseState.Generating, "\r\n[In progress]")]
    public void ExactSnapshotPreservesUnicodeMultilineTextTimestampsAndState(ResponseState state, string marker)
    {
        var turn = new ChatTurn("  one\n\u6f22\U0001f680\t\r\n\r\n  ")
        {
            Response = "partial \u20ac\r\nsecond\n\n",
            State = state
        };
        foreach (var timestamps in new[] { false, true })
        {
            var stamp = timestamps
                ? " [" + turn.SubmittedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + "]"
                : "";
            Assert.Equal("User" + stamp + ": " + turn.Prompt + "\r\nAssistant" + stamp +
                ": partial \u20ac\r\nsecond" + marker + "\r\n",
                TranscriptDisplay.Format([turn], timestamps, markGenerating: true));
            Assert.Empty(TranscriptDisplay.Format([], timestamps, markGenerating: true));
        }
        Assert.Equal("  one\n\u6f22\U0001f680\t\r\n\r\n  ", turn.Prompt);
        Assert.Equal("partial \u20ac\r\nsecond\n\n", turn.Response);
        Assert.Equal(state, turn.State);
        Assert.Null(turn.FinishedAt);
    }

    [Fact]
    public void SnapshotIsImmutableAndIncludesOnlySubmittedCurrentTurns()
    {
        var turns = new List<ChatTurn>
        {
            new("first") { Response = "complete", State = ResponseState.Completed },
            new("second") { Response = "partial" }
        };
        var snapshot = TranscriptDisplay.Format(turns, false, markGenerating: true);
        turns[1].Response += " later";
        turns[1].State = ResponseState.Completed;
        turns.Clear();
        Assert.Equal("User: first\r\nAssistant: complete\r\n" +
            "User: second\r\nAssistant: partial\r\n[In progress]\r\n", snapshot);
        Assert.Empty(TranscriptDisplay.Format(turns, false, markGenerating: true));
    }

    [Theory]
    [InlineData(ResponseState.Failed, "[Failed]")]
    [InlineData(ResponseState.Cancelled, "[Cancelled]")]
    [InlineData(ResponseState.Generating, "[In progress]")]
    public void EmptyPartialResponseStillHasExplicitState(ResponseState state, string marker)
    {
        var turn = new ChatTurn("submitted") { State = state };
        Assert.Equal("User: submitted\r\nAssistant: " + marker + "\r\n",
            TranscriptDisplay.Format([turn], false, markGenerating: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("User:\r\n\u6f22\U0001f680\nline\t two\r\n\r\nAssistant:\r\n\u20ac\r\n[Cancelled]\r\n\r\n")]
    public async Task WritesExactUtf8BytesAndReplacesConfirmedDestination(string snapshot)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Destination, "old content which must not remain");
        await TranscriptExport.WriteAsync(fixture.Destination, snapshot, CancellationToken.None);
        Assert.Equal(new UTF8Encoding(false, true).GetBytes(snapshot), File.ReadAllBytes(fixture.Destination));
        Assert.Equal(snapshot, File.ReadAllText(fixture.Destination, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task CancelledWriteKeepsExistingDestinationAndCleansTemporaryFile()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Destination, "previous");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TranscriptExport.WriteAsync(fixture.Destination, "replacement", cancelled.Token));
        Assert.Equal("previous", File.ReadAllText(fixture.Destination));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task LockedDestinationAndInvalidEncodingReportFailureWithoutReplacingPreviousFile()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Destination, "previous");
        using (File.Open(fixture.Destination, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                TranscriptExport.WriteAsync(fixture.Destination, "new", CancellationToken.None));
            Assert.Contains(fixture.Destination, error.Message);
            Assert.Contains("permissions and disk space", error.Message);
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        }
        await Assert.ThrowsAsync<IOException>(() =>
            TranscriptExport.WriteAsync(fixture.Destination, "\ud800", CancellationToken.None));
        Assert.Equal("previous", File.ReadAllText(fixture.Destination));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task MissingDirectoryRelativePathAndDirectoryDestinationAreActionableFailures()
    {
        using var fixture = new Fixture();
        foreach (var path in new[] { Path.Combine(fixture.Root, "missing", "file.txt"), "relative.txt", fixture.Work })
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                TranscriptExport.WriteAsync(path, "synthetic", CancellationToken.None));
            Assert.Contains(path, error.Message);
            Assert.Contains("conversation has not been changed", error.Message);
        }
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "missing")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task ReadOnlyDestinationReportsPermissionsAndKeepsPreviousFile()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Destination, "previous");
        File.SetAttributes(fixture.Destination, FileAttributes.ReadOnly);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                TranscriptExport.WriteAsync(fixture.Destination, "new", CancellationToken.None));
            Assert.Contains("permissions", error.Message);
            Assert.Equal("previous", File.ReadAllText(fixture.Destination));
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        }
        finally { File.SetAttributes(fixture.Destination, FileAttributes.Normal); }
    }

    [Fact]
    public void NativeDialogUsesTextDefaultValidPathAndOverwriteConfirmation()
    {
        var dialog = TranscriptExport.CreateDialog();
        Assert.Equal("Save Transcript", dialog.Title);
        Assert.Equal("Text files (*.txt)|*.txt|All files (*.*)|*.*", dialog.Filter);
        Assert.Equal("txt", dialog.DefaultExt);
        Assert.True(dialog.AddExtension);
        Assert.Equal("Transcript.txt", dialog.FileName);
        Assert.True(dialog.CheckPathExists);
        Assert.True(dialog.OverwritePrompt);
    }

    [Fact]
    public void MenuCommandCancellationWritesNothingAndEmptyExportIsSupported() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var selections = 0;
        var accept = false;
        var window = new MainWindow(fixture.Controller(), owner =>
        {
            Assert.IsType<MainWindow>(owner);
            selections++;
            return accept ? fixture.Destination : null;
        });
        try
        {
            Assert.False(ShellCommands.SaveTranscript.CanExecute(null, window));
            await window.InitializeBackendAsync();
            var file = (MenuItem)((Menu)window.FindName("MainMenu")).Items[0];
            var save = (MenuItem)file.Items[0];
            Assert.Same(ShellCommands.SaveTranscript, save.Command);
            Assert.Same(window, save.CommandTarget);
            Assert.True(save.IsEnabled);
            var filesBefore = Directory.GetFiles(fixture.Root);
            ShellCommands.SaveTranscript.Execute(null, window);
            Assert.False(ShellCommands.SaveTranscript.CanExecute(null, window));
            await window.SaveTranscriptAsync();
            await WaitAsync(() => ShellCommands.SaveTranscript.CanExecute(null, window));
            Assert.Equal(1, selections);
            Assert.Equal(filesBefore, Directory.GetFiles(fixture.Root));
            Assert.Empty(window.Turns);
            accept = true;
            await window.SaveTranscriptAsync();
            Assert.Equal(2, selections);
            Assert.Empty(File.ReadAllBytes(fixture.Destination));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void DelayedStreamingAndNestedDialogKeepPreDialogSnapshotWithoutFreezingOrMutatingChat() => OnSta(async () =>
    {
        using var fixture = new Fixture("stream");
        var backend = fixture.Controller();
        Task? generation = null;
        var selections = 0;
        var responsive = false;
        var window = new MainWindow(backend, owner =>
        {
            selections++;
            Assert.False(ShellCommands.SaveTranscript.CanExecute(null, owner));
            ShellCommands.ShowTimestamp.Execute(null, owner);
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            var frame = new DispatcherFrame();
            var watch = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                responsive = true;
                if (generation!.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Assert.True(generation!.IsCompletedSuccessfully, "Streaming did not finish while the dialog loop was active.");
            return fixture.Destination;
        });
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            ((TextBox)window.FindName("PromptInput")).Text = "synthetic snapshot\n\u6f22";
            generation = window.SendAsync();
            await WaitAsync(() => window.Turns[0].Response == "first ");
            var expected = "User: synthetic snapshot\n\u6f22\r\nAssistant: first \r\n[In progress]\r\n";
            Assert.True(ShellCommands.SaveTranscript.CanExecute(null, window));
            await window.SaveTranscriptAsync();
            Assert.Equal(expected, File.ReadAllText(fixture.Destination));
            Assert.True(responsive);
            Assert.Equal(1, selections);
            Assert.Equal(ResponseState.Completed, window.Turns[0].State);
            Assert.Contains("second", window.Turns[0].Response);
            Assert.True(backend.Settings.ShowTimestamp);
            Assert.Equal(TranscriptDisplay.Format(window.Turns, true),
                ((TranscriptView)window.FindName("Transcript")).Text);
            Assert.Empty(((TextBox)window.FindName("PromptInput")).Text);
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
        }
        finally { await window.StopAsync(); window.Close(); }
    });

    [Fact]
    public void ClearWindowExportsOnlyNewConversationAndNeverUnsentInput() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var backend = fixture.Controller();
        var window = new MainWindow(backend, _ => fixture.Destination);
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            var input = (TextBox)window.FindName("PromptInput");
            input.Text = "retired synthetic conversation";
            await window.SendAsync();
            await window.ClearWindowAsync();
            input.Text = "unsent input";
            await window.SaveTranscriptAsync();
            Assert.Empty(File.ReadAllText(fixture.Destination));
            Assert.Equal("unsent input", input.Text);
            input.Text = "new synthetic conversation";
            await window.SendAsync();
            await window.SaveTranscriptAsync();
            var actual = File.ReadAllText(fixture.Destination);
            Assert.Equal(TranscriptDisplay.Format(window.Turns, false, markGenerating: true), actual);
            Assert.Contains("new synthetic conversation", actual);
            Assert.DoesNotContain("retired", actual);
            Assert.DoesNotContain("unsent", actual);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WriteFailureShowsActionableDialogAndPreservesConversationInputAndDestination() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var backend = fixture.Controller();
        var window = new MainWindow(backend, _ => fixture.Destination);
        var diagnostics = new List<string>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (var error in window.OwnedWindows.OfType<ErrorWindow>().ToArray())
            {
                var panel = (StackPanel)error.Content;
                diagnostics.Add(((TextBlock)panel.Children.OfType<ScrollViewer>().Single().Content).Text);
                error.Close();
            }
        };
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            var input = (TextBox)window.FindName("PromptInput");
            input.Text = "synthetic submitted";
            await window.SendAsync();
            input.Text = "unsent kept";
            var original = ((TranscriptView)window.FindName("Transcript")).Text;
            var identity = backend.Session!.Id;
            File.WriteAllText(fixture.Destination, "previous");
            window.Show();
            using (File.Open(fixture.Destination, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                timer.Start();
                await window.SaveTranscriptAsync();
                Assert.Contains(fixture.Destination, Assert.Single(diagnostics));
            }
            Assert.Equal("previous", File.ReadAllText(fixture.Destination));
            Assert.Equal(original, ((TranscriptView)window.FindName("Transcript")).Text);
            Assert.Equal("unsent kept", input.Text);
            Assert.Equal(identity, backend.Session!.Id);
            Assert.Equal(ResponseState.Completed, Assert.Single(window.Turns).State);
            Assert.True(ShellCommands.SaveTranscript.CanExecute(null, window));
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Fact]
    public void ExitSettlesQueuedExportWithoutOpeningDialogOrWriting() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var selected = false;
        var window = new MainWindow(fixture.Controller(), _ =>
        {
            selected = true;
            return fixture.Destination;
        });
        await window.InitializeBackendAsync();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var export = window.SaveTranscriptAsync();
        window.Close();
        await export;
        await WaitAsync(() => closed);
        Assert.False(selected);
        Assert.False(File.Exists(fixture.Destination));
        Assert.False(ShellCommands.SaveTranscript.CanExecute(null, window));
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Export readiness gate timed out.");
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Export test timed out.");
        Assert.Null(failure);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "winchat-export-" + Guid.NewGuid());
        public string Work => Path.Combine(Root, "work");
        public string Destination => Path.Combine(Root, "transcript \u6f22.txt");
        public Fixture(string scenario = "complete")
        {
            Directory.CreateDirectory(Work);
            File.WriteAllText(Path.Combine(Work, "chat-scenario"), scenario);
        }
        public BackendController Controller()
        {
            var store = new SettingsStore(Path.Combine(Root, "settings.json"));
            store.Save(new AppSettings { ExecutablePath = Path.Combine(AppContext.BaseDirectory, "Fake CLI", "WinChatCopilot.FakeCli.exe") });
            return new BackendController(store, new BackendDiscovery("", []), new CopilotReadiness(Root).PrepareAsync);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
