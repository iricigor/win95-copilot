using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class ChatTests
{
    private const string CleanResponse = "first \u20ac\u6f22\U0001f680\r\nsecond\tline\nthird";

    [Fact]
    public async Task ActualSubprocessStreamsBeforeExitAndRoundTripsSafePromptArguments()
    {
        using var fixture = new Fixture("stream");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string prompt = "  quotes \" ; & | < >\r\nUnicode \u6f22\U0001f680\nC:\\test path\\  ";
        var received = new StringBuilder();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new CopilotChat().SendAsync(fixture.Session, prompt, chunk =>
        {
            received.Append(chunk);
            first.TrySetResult();
        }, bound.Token);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
            Assert.Equal("first ", received.ToString());
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "chat.started"))));
            Assert.False(process.HasExited);
            var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(fixture.Work, "chat.arguments.json")))!;
            Assert.Equal("--prompt", args[^2]);
            Assert.Equal(prompt, args[^1]);
            Assert.Contains("--session-id=" + fixture.Session.Id, args);
            Assert.Contains("--available-tools=__winchat_text_only__", args);
            Assert.Contains("--disable-builtin-mcps", args);
            Assert.Contains("--no-custom-instructions", args);
            Assert.Contains("--deny-tool=shell", args);
            Assert.Contains("--deny-tool=write", args);
            Assert.DoesNotContain(args, arg => arg.StartsWith("--allow"));
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            Assert.Equal(CleanResponse, await run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(CleanResponse, received.ToString());
            Assert.True(process.HasExited);
        }
        finally
        {
            await bound.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (bound.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("final-only")]
    public async Task CompleteTextIsCleanWithoutDuplicateFinalOrDiagnosticEvents(string scenario)
    {
        using var fixture = new Fixture(scenario);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var text = new StringBuilder();
        var result = await new CopilotChat().SendAsync(fixture.Session, "synthetic", chunk => text.Append(chunk), bound.Token);
        Assert.Equal(CleanResponse, result);
        Assert.Equal(result, text.ToString());
        Assert.DoesNotContain("DIAGNOSTIC", result);
        Assert.DoesNotContain("hidden title", result);
        Assert.DoesNotContain('\u001b', result);
    }

    [Theory]
    [InlineData("pre-failure")]
    [InlineData("partial-failure")]
    [InlineData("malformed")]
    [InlineData("missing-fields")]
    [InlineData("oversized-event")]
    [InlineData("truncated")]
    [InlineData("tools")]
    [InlineData("captured-tools")]
    [InlineData("tool-activity")]
    [InlineData("tool-request")]
    [InlineData("session-error")]
    [InlineData("wrong-session")]
    [InlineData("failed-result")]
    [InlineData("mismatch")]
    [InlineData("duplicate-final")]
    [InlineData("no-model")]
    [InlineData("no-result")]
    [InlineData("stderr")]
    [InlineData("nonzero")]
    public async Task InvalidOrFailedTurnNeverReportsReadyOrLosesAlreadyPublishedText(string scenario)
    {
        using var fixture = new Fixture(scenario);
        File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = fixture.Controller();
        await backend.ConnectAsync(bound.Token);
        var text = new StringBuilder();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.SendAsync("synthetic", chunk => text.Append(chunk), bound.Token));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.DoesNotContain('\u001b', error.Message);
        Assert.Equal(BackendState.Failed, backend.State);
        Assert.Null(backend.Session);
        Assert.False(backend.IsBusy);
        if (scenario == "partial-failure") Assert.Equal(CleanResponse, text.ToString());
        if (scenario == "pre-failure") Assert.Empty(text.ToString());
    }

    [Fact]
    public async Task EmptyUnavailableAndConcurrentSubmissionStartNoAdditionalRequest()
    {
        using var fixture = new Fixture("stream");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = fixture.Controller();
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SendAsync("synthetic", _ => { }, bound.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => backend.SendAsync(" \r\n\t", _ => { }, bound.Token));
        Assert.False(File.Exists(Path.Combine(fixture.Work, "chat.started")));
        await backend.ConnectAsync(bound.Token);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = backend.SendAsync("synthetic", _ => first.TrySetResult(), bound.Token);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BackendState.Generating, backend.State);
            Assert.True(backend.IsBusy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SendAsync("duplicate", _ => { }, bound.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ConnectAsync(bound.Token));
            Assert.Throws<InvalidOperationException>(() => backend.Save(new AppSettings()));
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            Assert.Equal(CleanResponse, await run);
            Assert.Equal(BackendState.Ready, backend.State);
            Assert.Equal("synthetic", JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(fixture.Work, "chat.arguments.json")))![^1]);
        }
        finally
        {
            await bound.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (bound.IsCancellationRequested) { }
        }
    }

    [Fact]
    public void DispatcherRemainsResponsiveAndCommandsReflectDelayedStream() => OnSta(async () =>
    {
        using var fixture = new Fixture("stream");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            var input = (TextBox)window.FindName("PromptInput");
            var transcript = (TextBox)window.FindName("Transcript");
            input.Text = " \n";
            await window.SendAsync();
            Assert.Empty(window.Turns);
            input.Text = "synthetic\nmultiline";
            var run = window.SendAsync();
            await window.SendAsync();
            await WaitAsync(() => transcript.Text.Contains("first "));
            Assert.False(run.IsCompleted);
            Assert.Single(window.Turns);
            Assert.True(input.IsReadOnly);
            Assert.False(ShellCommands.Send.CanExecute(null, window));
            Assert.False(ShellCommands.Connect.CanExecute(null, window));
            Assert.False(ShellCommands.ConfigureBackend.CanExecute(null, window));
            Assert.False(ShellCommands.Stop.CanExecute(null, window));
            Assert.True(ShellCommands.About.CanExecute(null, window));
            Assert.Contains("generating", ((TextBlock)window.FindName("BackendStatus")).Text);
            var ticked = false;
            await window.Dispatcher.InvokeAsync(() => ticked = true);
            Assert.True(ticked);
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            await run;
            var turn = Assert.Single(window.Turns);
            Assert.Equal(CleanResponse, turn.Response);
            Assert.Equal(ResponseState.Completed, turn.State);
            Assert.NotNull(turn.FinishedAt);
            Assert.True(turn.FinishedAt >= turn.SubmittedAt);
            Assert.Equal("User:\r\nsynthetic\nmultiline\r\n\r\nAssistant:\r\n" + CleanResponse + "\r\n\r\n", transcript.Text);
            Assert.True(transcript.IsReadOnly);
            Assert.Empty(input.Text);
            Assert.False(input.IsReadOnly);
            input.Text = "next";
            Assert.True(ShellCommands.Send.CanExecute(null, window));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("pre-failure", "")]
    [InlineData("partial-failure", CleanResponse)]
    public void FailedSubmissionPreservesInputPartialTextAndShowsModalDiagnostic(string scenario, string expected) => OnSta(async () =>
    {
        using var fixture = new Fixture(scenario);
        File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
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
        timer.Start();
        try
        {
            await window.InitializeBackendAsync();
            window.Show();
            await backend.ConnectAsync(CancellationToken.None);
            ((TextBox)window.FindName("PromptInput")).Text = "keep synthetic input";
            await window.SendAsync();
            Assert.True(errorSeen);
            var turn = Assert.Single(window.Turns);
            Assert.Equal(expected, turn.Response);
            Assert.Equal(ResponseState.Failed, turn.State);
            Assert.NotNull(turn.Error);
            Assert.Contains("[Failed]", ((TextBox)window.FindName("Transcript")).Text);
            Assert.Equal("keep synthetic input", ((TextBox)window.FindName("PromptInput")).Text);
            Assert.False(ShellCommands.Send.CanExecute(null, window));
            Assert.True(ShellCommands.Connect.CanExecute(null, window));
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Fact]
    public void ExitSettlesNewlyOwnedGenerationBeforeWindowCloses() => OnSta(async () =>
    {
        using var fixture = new Fixture("stream");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            ((TextBox)window.FindName("PromptInput")).Text = "synthetic";
            var run = window.SendAsync();
            await WaitAsync(() => window.Turns[0].Response.Length != 0);
            using var owned = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "chat.started"))));
            window.Close();
            await run;
            await WaitAsync(() => closed);
            Assert.True(owned.HasExited);
            Assert.Equal(ResponseState.Cancelled, window.Turns[0].State);
            Assert.Equal("first ", window.Turns[0].Response);
            Assert.Equal(BackendState.Disconnected, backend.State);
        }
        finally { if (!closed) window.Close(); }
    });

    [Fact]
    public void ExecutableDisappearingAfterReadinessRetainsInputAndAllowsExplicitReconnect() => OnSta(async () =>
    {
        using var fixture = new Fixture("complete");
        var store = new SettingsStore(Path.Combine(fixture.Root, "settings.json"));
        store.Save(new AppSettings { ExecutablePath = fixture.Session.ExecutablePath });
        var backend = new BackendController(store, new BackendDiscovery("", []), (_, _) =>
            Task.FromResult(fixture.Session with { ExecutablePath = Path.Combine(fixture.Root, "removed.exe") }));
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
        timer.Start();
        try
        {
            await window.InitializeBackendAsync();
            window.Show();
            await backend.ConnectAsync(CancellationToken.None);
            ((TextBox)window.FindName("PromptInput")).Text = "retain after launch failure";
            await window.SendAsync();
            Assert.True(errorSeen);
            Assert.Equal(ResponseState.Failed, Assert.Single(window.Turns).State);
            Assert.Equal("retain after launch failure", ((TextBox)window.FindName("PromptInput")).Text);
            Assert.Equal(BackendState.Failed, backend.State);
            Assert.True(ShellCommands.Connect.CanExecute(null, window));
            Assert.True(ShellCommands.ConfigureBackend.CanExecute(null, window));
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Theory]
    [InlineData("\u001b[31mred\u001b[0m", "red")]
    [InlineData("a\u001b]0;hidden\u001b\\b", "ab")]
    [InlineData("a\u001bPprivate\u001b\\b", "ab")]
    [InlineData("a\u009b31mb\u009b0m", "ab")]
    [InlineData("a\u001b(Bb\a\0\r\n\t", "ab\r\n\t")]
    public void TerminalSequencesAreRemovedAcrossEveryPossibleChunkBoundary(string raw, string clean)
    {
        for (var split = 0; split <= raw.Length; split++)
        {
            var filter = new TerminalTextFilter();
            Assert.Equal(clean, filter.Append(raw[..split]) + filter.Append(raw[split..]));
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Chat readiness gate timed out.");
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Chat dispatcher test timed out.");
        Assert.Null(failure);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "winchat-chat-" + Guid.NewGuid());
        public string Work => Path.Combine(Root, "work");
        public BackendSession Session { get; }
        public Fixture(string scenario)
        {
            Directory.CreateDirectory(Work);
            Directory.CreateDirectory(Path.Combine(Root, "cli"));
            var executable = Path.Combine(AppContext.BaseDirectory, "Fake CLI", "WinChatCopilot.FakeCli.exe");
            Session = new BackendSession(executable, Guid.NewGuid(), Path.Combine(Root, "cli"), Work);
            File.WriteAllText(Path.Combine(Work, "chat-scenario"), scenario);
        }
        public BackendController Controller()
        {
            var store = new SettingsStore(Path.Combine(Root, "settings.json"));
            store.Save(new AppSettings { ExecutablePath = Session.ExecutablePath });
            var backend = new BackendController(store, new BackendDiscovery("", []), new CopilotReadiness(Root).PrepareAsync);
            backend.Load();
            return backend;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
