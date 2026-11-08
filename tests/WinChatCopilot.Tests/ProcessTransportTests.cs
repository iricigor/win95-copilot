using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using WinChatCopilot.Processes;
using Xunit.Sdk;

namespace WinChatCopilot.Tests;

public class ProcessTransportTests
{
    [Fact]
    public async Task StreamsBeforeExitWithoutNewlinesAndDecodesSplitUtf8()
    {
        await using var fixture = new ProcessFixture();
        var first = Signal();
        var chunks = new ConcurrentQueue<string>();
        var run = fixture.Run("stream", chunk =>
        {
            chunks.Enqueue(chunk);
            first.TrySetResult();
        });

        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("first ", string.Concat(chunks));
        Assert.False(run.IsCompleted);
        fixture.Release("continue");
        await fixture.WaitForFile("unicode-prefix");
        Assert.Equal("first ", string.Concat(chunks));
        fixture.Release("continue-unicode");

        Assert.Equal(0, await run);
        Assert.True(chunks.Count >= 2);
        Assert.Equal("first \u20ac\u6f22\U0001f680\nsecond\r\nthird", string.Concat(chunks));
        Assert.Equal("diagnostic only", fixture.Error);
    }

    [Theory]
    [InlineData("exit", 0, "complete", "")]
    [InlineData("nonzero", 17, "", "synthetic failure")]
    [InlineData("formatting", 0, "one \u001b[31mtwo\u001b[0m\r\nthree", "\u001b[33mwarning\u001b[0m")]
    public async Task KeepsRawOutputDiagnosticsAndExitCodeSeparate(
        string scenario, int exitCode, string output, string error)
    {
        await using var fixture = new ProcessFixture();
        Assert.Equal(exitCode, await fixture.Run(scenario));
        Assert.Equal(output, fixture.Output);
        Assert.Equal(error, fixture.Error);
    }

    [Fact]
    public async Task RetainsProgressivePartialOutputOnNonzeroExit()
    {
        await using var fixture = new ProcessFixture();
        var first = Signal();
        var run = fixture.Run("partial-failure", _ => first.TrySetResult());
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("partial response", fixture.Output);
        Assert.False(run.IsCompleted);
        fixture.Release("continue");

        Assert.Equal(23, await run);
        Assert.Equal("partial response", fixture.Output);
        Assert.Equal("failure after output", fixture.Error);
    }

    [Fact]
    public async Task DrainsStderrWhileStdoutRemainsOpenWithoutDeadlocking()
    {
        await using var fixture = new ProcessFixture();
        var drained = Signal();
        var run = fixture.Run("stderr-pressure", _ =>
        {
            if (fixture.Output.EndsWith("|drained", StringComparison.Ordinal))
            {
                drained.TrySetResult();
            }
        });
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(run.IsCompleted);
        fixture.Release("continue");
        Assert.Equal(0, await run);
        Assert.Equal("ready|drained", fixture.Output);
        Assert.Equal(new string('d', 256 * 1024), fixture.Error);
    }

    [Fact]
    public async Task ArgumentsPromptSessionEnvironmentAndClosedInputRoundTrip()
    {
        await using var fixture = new ProcessFixture();
        var session = Guid.NewGuid().ToString();
        var prompt = "synthetic \"quoted\" text & | >\nnext line \u20ac\u6f22\U0001f680 C:\\space dir\\";
        string[] arguments = [$"--session-id={session}", "--prompt", prompt, "", "trailing\\"];
        Assert.Contains(' ', fixture.StartInfo("inspect").FileName);

        for (var turn = 0; turn < 2; turn++)
        {
            Assert.Equal(0, await fixture.Run("inspect", arguments: arguments));
            var actual = JsonSerializer.Deserialize<Invocation>(fixture.Output)!;
            Assert.Equal(arguments, actual.Arguments);
            Assert.Equal(fixture.DirectoryPath, actual.WorkingDirectory);
            Assert.True(actual.InputAtEof);
            Assert.Equal("synthetic fixture", actual.FixtureValue);
            Assert.Equal("", actual.Path);
        }
    }

    [Theory]
    [InlineData("wrong-session")]
    [InlineData("wrong-prompt")]
    public async Task InvocationAssertionsDetectIntentionallyWrongFakeBehavior(string scenario)
    {
        await using var fixture = new ProcessFixture();
        string[] expected = [$"--session-id={Guid.NewGuid()}", "--prompt", "synthetic prompt"];
        Assert.Equal(0, await fixture.Run(scenario, arguments: expected));
        var actual = JsonSerializer.Deserialize<Invocation>(fixture.Output)!;
        Assert.Throws<EqualException>(() => Assert.Equal(expected, actual.Arguments));
    }

    [Theory]
    [InlineData("stall")]
    [InlineData("silent-stall")]
    public async Task CancellationBeforeOrAfterOutputSettlesOwnedProcess(string scenario)
    {
        await using var fixture = new ProcessFixture();
        var first = Signal();
        var run = fixture.Run(scenario, _ => first.TrySetResult());
        await fixture.WaitForFile("parent.pid");
        using var owned = fixture.OpenProcess("parent.pid");
        if (scenario == "stall")
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(run.IsCompleted);
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.True(owned.HasExited);
        Assert.Equal(scenario == "stall" ? "ready" : "", fixture.Output);
    }

    [Fact]
    public async Task ExplicitTestTimeoutStopsStallWithoutInventingProductLatencyPolicy()
    {
        await using var fixture = new ProcessFixture();
        var run = fixture.Run("silent-stall");
        await fixture.WaitForFile("parent.pid");
        using var owned = fixture.OpenProcess("parent.pid");
        fixture.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.True(owned.HasExited);
        Assert.Empty(fixture.Output);
    }

    [Fact]
    public async Task CancellationStopsOwnedChildButNotUnrelatedSentinel()
    {
        await using var sentinel = new ProcessFixture();
        var sentinelReady = Signal();
        var sentinelRun = sentinel.Run("sentinel", _ => sentinelReady.TrySetResult());
        await sentinelReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var unrelated = sentinel.OpenProcess("parent.pid");

        await using var fixture = new ProcessFixture();
        var childReady = Signal();
        var run = fixture.Run("with-child", _ => childReady.TrySetResult());
        await childReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var parent = fixture.OpenProcess("parent.pid");
        using var child = fixture.OpenProcess("child.pid");
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(parent.HasExited);
        Assert.True(child.HasExited);
        Assert.False(unrelated.HasExited);
        Assert.False(sentinelRun.IsCompleted);

        sentinel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sentinelRun.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.True(unrelated.HasExited);
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("utf16")]
    public async Task InvalidEncodingFailsExplicitlyAndStopsProcess(string scenario)
    {
        await using var fixture = new ProcessFixture();
        var run = fixture.Run(scenario);
        await fixture.WaitForFile("parent.pid");
        using var owned = fixture.OpenProcess("parent.pid");
        fixture.Release("continue");
        await Assert.ThrowsAsync<DecoderFallbackException>(() => run);
        Assert.True(owned.HasExited);
        Assert.Empty(fixture.Output);
    }

    [Fact]
    public async Task ConsumerFailureStopsProcessAndIsNotReportedAsSuccess()
    {
        await using var fixture = new ProcessFixture();
        using var started = new ManualResetEventSlim();
        var run = fixture.Run("stall", _ =>
        {
            started.Wait(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("Synthetic consumer failure.");
        });
        await fixture.WaitForFile("parent.pid");
        using var owned = fixture.OpenProcess("parent.pid");
        started.Set();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("Synthetic consumer failure.", failure.Message);
        Assert.True(owned.HasExited);
    }

    [Fact]
    public async Task PreCancelledInvocationStartsNothing()
    {
        await using var fixture = new ProcessFixture();
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run("exit"));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "parent.pid")));
    }

    [Fact]
    public async Task MissingExecutableAndRawCommandStringFailExplicitly()
    {
        await using var fixture = new ProcessFixture();
        var missing = fixture.StartInfo("exit");
        missing.FileName = Path.Combine(fixture.DirectoryPath, "missing.exe");
        await Assert.ThrowsAsync<Win32Exception>(() =>
            ProcessTransport.RunAsync(missing, _ => { }, _ => { }, CancellationToken.None));

        var raw = fixture.StartInfo("exit");
        raw.ArgumentList.Clear();
        raw.Arguments = "exit";
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ProcessTransport.RunAsync(raw, _ => { }, _ => { }, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "parent.pid")));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Invocation(
        string[] Arguments, string WorkingDirectory, bool InputAtEof, string? FixtureValue, string? Path);

    private sealed class ProcessFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        private readonly ConcurrentQueue<string> output = new();
        private readonly ConcurrentQueue<string> error = new();
        private Task<int>? run;

        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(), $"WinChat process tests {Guid.NewGuid():N}");

        public string Output => string.Concat(output);
        public string Error => string.Concat(error);

        public ProcessFixture() => Directory.CreateDirectory(DirectoryPath);

        public ProcessStartInfo StartInfo(string scenario)
        {
            var info = new ProcessStartInfo(Path.Combine(
                AppContext.BaseDirectory, "Fake CLI", "WinChatCopilot.FakeCli.exe"))
            {
                WorkingDirectory = DirectoryPath
            };
            info.ArgumentList.Add(scenario);
            info.Environment["PATH"] = "";
            info.Environment["WINCHAT_TEST_VALUE"] = "synthetic fixture";
            info.Environment.Remove("GITHUB_TOKEN");
            info.Environment.Remove("GH_TOKEN");
            info.Environment.Remove("COPILOT_GITHUB_TOKEN");
            return info;
        }

        public Task<int> Run(string scenario, Action<string>? receive = null, string[]? arguments = null)
        {
            Assert.True(run is null || run.IsCompleted, "A fixture must not overlap requests.");
            output.Clear();
            error.Clear();
            var info = StartInfo(scenario);
            foreach (var argument in arguments ?? [])
            {
                info.ArgumentList.Add(argument);
            }

            run = ProcessTransport.RunAsync(info, chunk =>
            {
                output.Enqueue(chunk);
                receive?.Invoke(chunk);
            }, error.Enqueue, timeout.Token);
            return run;
        }

        public void Release(string name) => File.WriteAllText(Path.Combine(DirectoryPath, name), "continue");
        public void Cancel() => timeout.Cancel();
        public void CancelAfter(TimeSpan delay) => timeout.CancelAfter(delay);

        public async Task WaitForFile(string name)
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(DirectoryPath, name)))
            {
                await Task.Delay(20, bound.Token);
            }
        }

        public Process OpenProcess(string name) => Process.GetProcessById(
            int.Parse(File.ReadAllText(Path.Combine(DirectoryPath, name))));

        public async ValueTask DisposeAsync()
        {
            await timeout.CancelAsync();
            if (run is { IsCompleted: false })
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(12));
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                }
            }

            timeout.Dispose();
            FixtureDirectory.Delete(DirectoryPath);
        }
    }
}
