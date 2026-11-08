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
        var session = backend.Session;
        var text = new StringBuilder();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.SendAsync("synthetic", chunk => text.Append(chunk), bound.Token));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.DoesNotContain('\u001b', error.Message);
        Assert.Equal(BackendState.Failed, backend.State);
        Assert.Same(session, backend.Session);
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
            var transcript = (TranscriptView)window.FindName("Transcript");
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
            Assert.True(ShellCommands.Stop.CanExecute(null, window));
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
            Assert.Equal("User: synthetic\nmultiline\r\nAssistant: " + CleanResponse + "\r\n",
                transcript.Text);
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
            Assert.Contains("[Failed]", ((TranscriptView)window.FindName("Transcript")).Text);
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

    [Theory]
    [InlineData("silent-stall", "")]
    [InlineData("stream", "first ")]
    [InlineData("stall", "first ")]
    public async Task StopSettlesBeforeAnotherSendAndRetainsSessionWithoutRetry(string scenario, string expected)
    {
        using var fixture = new Fixture(scenario);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = fixture.Controller();
        await backend.ConnectAsync(bound.Token);
        var session = backend.Session;
        var text = new StringBuilder();
        var run = backend.SendAsync("synthetic cancellation", chunk => text.Append(chunk), bound.Token);
        try
        {
            await WaitAsync(() => File.Exists(Path.Combine(fixture.Work, "chat.started")) && text.Length >= expected.Length);
            using var owned = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "chat.started"))));
            backend.CancelGeneration();
            Assert.Equal(BackendState.Cancelling, backend.State);
            Assert.True(backend.IsBusy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SendAsync("duplicate", _ => { }, bound.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ConnectAsync(bound.Token));
            Assert.Throws<InvalidOperationException>(() => backend.Save(new AppSettings()));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(owned.HasExited);
            Assert.Equal(expected, text.ToString());
            Assert.Equal(BackendState.Ready, backend.State);
            Assert.Same(session, backend.Session);
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
            File.WriteAllText(Path.Combine(fixture.Work, "chat-scenario"), "complete");
            Assert.Equal(CleanResponse, await backend.SendAsync("explicit follow-up, not retry", _ => { }, bound.Token));
            var requests = File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl"))
                .Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
            Assert.Equal(2, requests.Length);
            Assert.All(requests, args => Assert.Contains("--session-id=" + session!.Id, args));
            Assert.Equal("explicit follow-up, not retry", requests[1][^1]);
        }
        finally
        {
            await bound.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (bound.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("partial-failure")]
    [InlineData("nonzero")]
    public async Task ExplicitRecoveryVerifiesSameSessionAndDoesNotResubmitFailedPrompt(string scenario)
    {
        using var fixture = new Fixture(scenario);
        File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = fixture.Controller();
        await backend.ConnectAsync(bound.Token);
        var id = backend.Session!.Id;
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.SendAsync("accepted synthetic prompt", _ => { }, bound.Token));
        Assert.Equal(BackendState.Failed, backend.State);
        Assert.Equal(id, backend.Session!.Id);
        File.WriteAllText(Path.Combine(fixture.Work, "readiness-scenario"), "auth");
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.ConnectAsync(bound.Token));
        Assert.Equal(BackendState.Failed, backend.State);
        Assert.Equal(id, backend.Session!.Id);
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
        File.WriteAllText(Path.Combine(fixture.Work, "readiness-scenario"), "ready");
        await backend.ConnectAsync(bound.Token);
        Assert.Equal(BackendState.Ready, backend.State);
        Assert.Equal(id, backend.Session!.Id);
        var check = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(fixture.Work, "readiness.arguments.json")))!;
        Assert.Contains("--session-id=" + id, check);
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
        File.WriteAllText(Path.Combine(fixture.Work, "chat-scenario"), "complete");
        await backend.SendAsync("new explicit prompt", _ => { }, bound.Token);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")).Length);
    }

    [Fact]
    public async Task RecoveryCannotReplaceIdentityOrChangeBackendWithoutExplicitReset()
    {
        using var fixture = new Fixture("complete");
        var store = new SettingsStore(Path.Combine(fixture.Root, "settings.json"));
        store.Save(new AppSettings { ExecutablePath = fixture.Session.ExecutablePath });
        var backend = new BackendController(store, new BackendDiscovery("", []),
            (_, _) => Task.FromResult(fixture.Session),
            (session, _) => Task.FromResult(session with { Id = Guid.NewGuid() }));
        backend.Load();
        await backend.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ConnectAsync(CancellationToken.None));
        Assert.Equal(BackendState.Failed, backend.State);
        Assert.Same(fixture.Session, backend.Session);
        Assert.Throws<InvalidOperationException>(() => backend.Save(backend.Settings with { ExecutablePath = null }));
        Assert.Equal(fixture.Session.ExecutablePath, store.Load().ExecutablePath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ready = fixture.Controller();
        await ready.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ready.SendAsync("pre-cancelled", _ => throw new InvalidOperationException("No callback expected."), cancelled.Token));
        Assert.False(File.Exists(Path.Combine(fixture.Work, "chat.started")));
        Assert.False(ready.IsBusy);
    }

    [Fact]
    public void StopCommandPreservesPartialTurnAndInputThenIgnoresRetiredUpdates() => OnSta(async () =>
    {
        using var fixture = new Fixture("stream");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            var input = (TextBox)window.FindName("PromptInput");
            var transcript = (TranscriptView)window.FindName("Transcript");
            input.Text = "synthetic cancelled input";
            var run = window.SendAsync();
            await WaitAsync(() => window.Turns[0].Response.Length != 0);
            Assert.True(ShellCommands.Stop.CanExecute(null, window));
            ShellCommands.Stop.Execute(null, window);
            Assert.False(ShellCommands.Stop.CanExecute(null, window));
            Assert.False(ShellCommands.Send.CanExecute(null, window));
            Assert.Contains("Cancelling", ((TextBlock)window.FindName("BackendStatus")).Text);
            await run;
            var old = Assert.Single(window.Turns);
            Assert.Equal(ResponseState.Cancelled, old.State);
            Assert.Equal("first ", old.Response);
            Assert.NotNull(old.FinishedAt);
            Assert.Contains("[Cancelled]", transcript.Text);
            Assert.Equal("synthetic cancelled input", input.Text);
            Assert.False(input.IsReadOnly);
            Assert.Contains("Same session retained", ((TextBlock)window.FindName("BackendStatus")).Text);
            File.WriteAllText(Path.Combine(fixture.Work, "chat-scenario"), "complete");
            input.Text = "different explicit prompt";
            await window.SendAsync();
            var snapshot = transcript.Text;
            await Task.Delay(100);
            Assert.Equal(snapshot, transcript.Text);
            Assert.Equal("first ", old.Response);
            Assert.Equal(CleanResponse, window.Turns[1].Response);
            Assert.Equal(ResponseState.Completed, window.Turns[1].State);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")).Length);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("file")]
    [InlineData("system")]
    [InlineData("window")]
    public void EveryClosingPathRejectsReentryAndSettlesOwnedChildNotSentinel(string path) => OnSta(async () =>
    {
        using var fixture = new Fixture("with-child");
        using var sentinelFixture = new Fixture("complete");
        using var sentinel = Process.Start(new ProcessStartInfo(sentinelFixture.Session.ExecutablePath)
        {
            ArgumentList = { "sentinel" }, WorkingDirectory = sentinelFixture.Work, UseShellExecute = false, CreateNoWindow = true
        })!;
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            ((TextBox)window.FindName("PromptInput")).Text = "synthetic owned shutdown";
            var run = window.SendAsync();
            await WaitAsync(() => window.Turns[0].Response.Length != 0);
            using var parent = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "chat.started"))));
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "child.pid"))));
            if (path == "file")
            {
                var exit = (MenuItem)((MenuItem)((Menu)window.FindName("MainMenu")).Items[0]).Items[3];
                ((System.Windows.Input.RoutedCommand)exit.Command).Execute(null, exit.CommandTarget);
            }
            else if (path == "system") SystemCommands.CloseWindowCommand.Execute(null, window);
            else window.Close();
            window.Close();
            Assert.False(closed);
            Assert.False(ShellCommands.Send.CanExecute(null, window));
            await run;
            await WaitAsync(() => closed);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(parent.HasExited);
            Assert.True(child.HasExited);
            Assert.False(sentinel.HasExited);
            Assert.Equal(ResponseState.Cancelled, Assert.Single(window.Turns).State);
            Assert.Contains("[Cancelled]", ((TranscriptView)window.FindName("Transcript")).Text);
        }
        finally
        {
            if (!closed) { window.Close(); await WaitAsync(() => closed); }
            if (!sentinel.HasExited) sentinel.Kill(entireProcessTree: true);
            await sentinel.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    });

    [Theory]
    [InlineData("complete")]
    [InlineData("stream")]
    [InlineData("with-child")]
    public void ClearWindowMenuSettlesOldWorkAndOnlyNewPromptsUseFreshIdentity(string scenario) => OnSta(async () =>
    {
        using var fixture = new Fixture(scenario);
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            var oldId = backend.Session!.Id;
            var input = (TextBox)window.FindName("PromptInput");
            var transcript = (TranscriptView)window.FindName("Transcript");
            input.Text = "first synthetic turn";
            var generation = window.SendAsync();
            await WaitAsync(() => window.Turns[0].Response.Length != 0);
            if (scenario == "complete")
            {
                await generation;
                input.Text = "ordinary follow-up";
                await window.SendAsync();
                Assert.Equal(oldId, backend.Session!.Id);
            }
            var oldTurn = window.Turns[0];
            using var parent = scenario == "complete" ? null
                : Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "chat.started"))));
            using var child = scenario == "with-child"
                ? Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "child.pid")))) : null;
            var menu = (MenuItem)((MenuItem)((Menu)window.FindName("MainMenu")).Items[0]).Items[1];
            Assert.Same(ShellCommands.ClearWindow, menu.Command);
            var command = (System.Windows.Input.RoutedCommand)menu.Command;
            Assert.True(command.CanExecute(null, menu.CommandTarget));
            command.Execute(null, menu.CommandTarget);
            Assert.False(ShellCommands.Send.CanExecute(null, window));
            Assert.False(ShellCommands.ClearWindow.CanExecute(null, window));
            await window.ClearWindowAsync();
            await window.SendAsync();
            await generation;
            await WaitAsync(() => backend.State == BackendState.Ready && window.Turns.Count == 0 &&
                ShellCommands.ClearWindow.CanExecute(null, window));
            if (parent is not null) Assert.True(parent.HasExited);
            if (child is not null)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(child.HasExited);
            }
            var freshId = backend.Session!.Id;
            Assert.NotEqual(oldId, freshId);
            Assert.Empty(transcript.Text);
            if (scenario != "complete")
            {
                Assert.Equal(ResponseState.Cancelled, oldTurn.State);
                Assert.Equal("first synthetic turn", input.Text);
            }
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            File.WriteAllText(Path.Combine(fixture.Work, "chat-scenario"), "complete");
            input.Text = "only explicit new conversation prompt";
            await window.SendAsync();
            var snapshot = transcript.Text;
            await Task.Delay(100);
            Assert.Equal(snapshot, transcript.Text);
            Assert.Single(window.Turns);
            Assert.DoesNotContain("first synthetic turn", transcript.Text);
            var requests = File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl"))
                .Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
            Assert.Equal(scenario == "complete" ? 3 : 2, requests.Length);
            Assert.All(requests[..^1], args => Assert.Contains("--session-id=" + oldId, args));
            Assert.Contains("--session-id=" + freshId, requests[^1]);
            Assert.Equal("only explicit new conversation prompt", requests[^1][^1]);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FailedResetClearsHistoryReportsErrorAndReconnectCannotFallBack() => OnSta(async () =>
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
        timer.Start();
        try
        {
            await window.InitializeBackendAsync();
            window.Show();
            await backend.ConnectAsync(CancellationToken.None);
            var oldId = backend.Session!.Id;
            ((TextBox)window.FindName("PromptInput")).Text = "old conversation";
            await window.SendAsync();
            File.WriteAllText(Path.Combine(fixture.Work, "readiness-scenario"), "auth");
            await window.ClearWindowAsync();
            Assert.True(errorSeen);
            Assert.Empty(window.Turns);
            Assert.Empty(((TranscriptView)window.FindName("Transcript")).Text);
            Assert.Null(backend.Session);
            Assert.Equal(BackendState.Failed, backend.State);
            Assert.Contains("new conversation preparation failed", ((TextBlock)window.FindName("BackendStatus")).Text);
            ((TextBox)window.FindName("PromptInput")).Text = "unsent new prompt";
            await window.SendAsync();
            Assert.Single(File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl")));
            File.WriteAllText(Path.Combine(fixture.Work, "readiness-scenario"), "ready");
            await backend.ConnectAsync(CancellationToken.None);
            Assert.NotEqual(oldId, backend.Session!.Id);
            Assert.Empty(window.Turns);
            Assert.Equal("unsent new prompt", ((TextBox)window.FindName("PromptInput")).Text);
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Fact]
    public async Task ResetRejectsBusyOrReusedIdentityAndGatesPreparationWithoutFallback()
    {
        using var fixture = new Fixture("complete");
        var store = new SettingsStore(Path.Combine(fixture.Root, "settings.json"));
        store.Save(new AppSettings { ExecutablePath = fixture.Session.ExecutablePath });
        var next = new TaskCompletionSource<BackendSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var backend = new BackendController(store, new BackendDiscovery("", []),
            (_, _) => ++calls == 1 ? Task.FromResult(fixture.Session) : next.Task);
        backend.Load();
        await backend.ConnectAsync(CancellationToken.None);
        var reset = backend.ResetAsync(CancellationToken.None);
        Assert.Null(backend.Session);
        Assert.True(backend.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ResetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ConnectAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SendAsync("racing prompt", _ => { }, CancellationToken.None));
        next.SetResult(fixture.Session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reset);
        Assert.Null(backend.Session);
        Assert.Equal(BackendState.Failed, backend.State);
        Assert.Equal(2, calls);
        Assert.False(File.Exists(Path.Combine(fixture.Work, "chat.started")));
    }

    [Fact]
    public void ExitDuringResetSettlesReadinessAndNeverAcceptsLateReadyState() => OnSta(async () =>
    {
        using var fixture = new Fixture("complete");
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            File.Delete(Path.Combine(fixture.Work, "readiness.started"));
            File.WriteAllText(Path.Combine(fixture.Work, "readiness-scenario"), "stall");
            var reset = window.ClearWindowAsync();
            await WaitAsync(() => File.Exists(Path.Combine(fixture.Work, "readiness.started")));
            using var owned = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Work, "readiness.started"))));
            Assert.True(((TextBox)window.FindName("PromptInput")).IsReadOnly);
            Assert.False(ShellCommands.Connect.CanExecute(null, window));
            Assert.False(ShellCommands.ClearWindow.CanExecute(null, window));
            window.Close();
            window.Close();
            await reset;
            await WaitAsync(() => closed);
            Assert.True(owned.HasExited);
            Assert.Null(backend.Session);
            Assert.Equal(BackendState.Disconnected, backend.State);
            Assert.Empty(window.Turns);
        }
        finally { if (!closed) window.Close(); }
    });

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
