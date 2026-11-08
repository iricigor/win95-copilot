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
public class BackendTests
{
    [Fact]
    public void DefaultsAndAllPreferencesRoundTripWithoutCredentials()
    {
        using var fixture = new Fixture();
        var defaults = fixture.Store.Load();
        Assert.Equal(SendMode.ControlEnter, defaults.SendMode);
        Assert.Null(defaults.ExecutablePath);
        Assert.False(defaults.AutoConnect);
        Assert.False(defaults.WordWrap);
        Assert.False(defaults.ShowTimestamp);
        var settings = defaults with
        {
            ExecutablePath = fixture.Executable, SendMode = SendMode.Enter,
            WordWrap = true, AutoConnect = true, ShowTimestamp = true
        };
        fixture.Store.Save(settings);
        Assert.Equal(settings, new SettingsStore(fixture.Store.FilePath).Load());
        Assert.DoesNotContain("token", File.ReadAllText(fixture.Store.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":2}")]
    [InlineData("{\"SendMode\":\"Invalid\"}")]
    [InlineData("{\"SendMode\":123}")]
    [InlineData("{\"AutoConnect\":\"yes\"}")]
    [InlineData("{\"ExecutablePath\":\" \"}")]
    [InlineData("{\"Credential\":\"never supported\"}")]
    public void MalformedSettingsAreNotSilentlyReplaced(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.FilePath, json);
        Assert.Contains(fixture.Store.FilePath, Assert.Throws<IOException>(() => fixture.Store.Load()).Message);
        Assert.Equal(json, File.ReadAllText(fixture.Store.FilePath));
    }

    [Fact]
    public void UnreadableAndReadOnlySettingsHaveActionableErrorsAndKeepPriorData()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(new AppSettings());
        using (File.Open(fixture.Store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("Cannot read", Assert.Throws<IOException>(() => fixture.Store.Load()).Message);
        File.SetAttributes(fixture.Store.FilePath, FileAttributes.ReadOnly);
        try
        {
            Assert.Contains("Cannot save", Assert.Throws<IOException>(() =>
                fixture.Store.Save(new AppSettings { AutoConnect = true })).Message);
            Assert.False(fixture.Store.Load().AutoConnect);
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        }
        finally { File.SetAttributes(fixture.Store.FilePath, FileAttributes.Normal); }
    }

    [Fact]
    public void DiscoveryUsesFirstPathThenVerifiedRootsAndNeverReplacesExplicitSelection()
    {
        using var fixture = new Fixture();
        var first = Path.Combine(fixture.Root, "first path");
        var second = Path.Combine(fixture.Root, "second path");
        var packages = Path.Combine(fixture.Root, "packages");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(Path.Combine(packages, "GitHub.Copilot_synthetic-source"));
        var firstExe = Path.Combine(first, "copilot.exe");
        var secondExe = Path.Combine(second, "copilot.exe");
        var packageExe = Path.Combine(packages, "GitHub.Copilot_synthetic-source", "copilot.exe");
        File.WriteAllText(firstExe, "");
        File.WriteAllText(secondExe, "");
        File.WriteAllText(packageExe, "");
        var discovery = new BackendDiscovery($"\"{first}\";{second}", [packages]);
        Assert.Equal(firstExe, discovery.Resolve(null));
        Assert.Equal(secondExe, discovery.Resolve(secondExe));
        Assert.Throws<FileNotFoundException>(() => discovery.Resolve(Path.Combine(first, "missing.exe")));
        Assert.Throws<FileNotFoundException>(() => discovery.Resolve("copilot.exe"));
        Assert.Throws<FileNotFoundException>(() => discovery.Resolve(first));
        Assert.Equal(packageExe, new BackendDiscovery("", [packages]).Resolve(null));
        Assert.Throws<FileNotFoundException>(() => new BackendDiscovery("", []).Resolve(null));
    }

    [Fact]
    public async Task RealFakeExecutableProvesReadinessArgumentsAndFreshIdentity()
    {
        using var fixture = new Fixture();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var readiness = new CopilotReadiness(fixture.Root);
        var first = await readiness.PrepareAsync(fixture.Executable, bound.Token);
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(fixture.Work, "readiness.arguments.json")))!;
        Assert.Equal($"--session-id={first.Id}", arguments[^3]);
        Assert.Equal("--prompt", arguments[^2]);
        Assert.Equal("Connection check only. Reply with READY. Do not use tools.", arguments[^1]);
        Assert.Contains("--available-tools=__winchat_text_only__", arguments);
        Assert.Contains("--deny-tool=shell", arguments);
        Assert.Contains("--deny-tool=write", arguments);
        Assert.Contains("--disable-builtin-mcps", arguments);
        Assert.Contains("--no-custom-instructions", arguments);
        Assert.DoesNotContain(arguments, arg => arg.StartsWith("--allow"));
        Assert.Equal(first.Home, File.ReadAllText(Path.Combine(fixture.Work, "readiness.home")));
        Assert.StartsWith(fixture.Root, first.Home);
        Assert.Equal(fixture.Executable, first.ExecutablePath);
        var second = await readiness.PrepareAsync(fixture.Executable, bound.Token);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("auth")]
    [InlineData("tools")]
    [InlineData("session")]
    [InlineData("malformed")]
    [InlineData("missing-fields")]
    [InlineData("no-evidence")]
    public async Task FailedOrIncompatibleReadinessNeverBecomesReady(string scenario)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Work, "readiness-scenario"), scenario);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = fixture.Controller();
        await Assert.ThrowsAsync<InvalidDataException>(() => backend.ConnectAsync(bound.Token));
        Assert.Equal(BackendState.Failed, backend.State);
        Assert.Null(backend.Session);
        Assert.False(backend.IsBusy);
    }

    [Fact]
    public async Task CancellationSettlesTheOwnedReadinessProcess()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Work, "readiness-scenario"), "stall");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backend = fixture.Controller();
        var run = backend.ConnectAsync(bound.Token);
        try
        {
            var file = Path.Combine(fixture.Work, "readiness.started");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(file))
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10));
                await Task.Delay(20);
            }
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(file)));
            Assert.True(backend.IsBusy);
            await bound.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.True(process.HasExited);
            Assert.Equal(BackendState.Disconnected, backend.State);
        }
        finally
        {
            await bound.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (OperationCanceledException) when (bound.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void StartupPreparesOnceAndManualReconnectVerifiesExistingIdentity(bool auto, int startupCalls) => OnSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.Save(new AppSettings { ExecutablePath = fixture.Executable, AutoConnect = auto });
        var calls = 0;
        var resumes = 0;
        var backend = new BackendController(fixture.Store, new BackendDiscovery("", []), (exe, _) =>
        {
            calls++;
            return Task.FromResult(new BackendSession(exe, Guid.NewGuid(), fixture.Root, fixture.Work));
        }, (session, _) =>
        {
            resumes++;
            return Task.FromResult(session);
        });
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            Assert.Equal(startupCalls, calls);
            Assert.True(ShellCommands.ConfigureBackend.CanExecute(null, window));
            Assert.True(ShellCommands.Connect.CanExecute(null, window));
            ShellCommands.Connect.Execute(null, window);
            ShellCommands.Connect.Execute(null, window);
            Assert.Equal(1, calls);
            Assert.Equal(startupCalls + 1, resumes);
            ShellCommands.AutoConnect.Execute(null, window);
            Assert.Equal(!auto, fixture.Store.Load().AutoConnect);
            Assert.False(ShellCommands.Send.CanExecute(null, window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MissingBackendStartupDialogCanCancelWithoutPreparation() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var calls = 0;
        var backend = new BackendController(fixture.Store, new BackendDiscovery("", []), (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Should not prepare.");
        });
        var window = new MainWindow(backend);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        var observed = false;
        timer.Tick += (_, _) =>
        {
            foreach (var owned in window.OwnedWindows.OfType<BackendSettingsWindow>().ToArray())
            {
                observed = true;
                Assert.Contains("not found", ((TextBlock)owned.FindName("ValidationMessage")).Text);
                owned.DialogResult = false;
                timer.Stop();
            }
        };
        timer.Start();
        try
        {
            window.Show();
            await window.InitializeBackendAsync();
            Assert.True(observed);
            Assert.Equal(0, calls);
            Assert.Equal(BackendState.Disconnected, backend.State);
            Assert.False(File.Exists(fixture.Store.FilePath));
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Fact]
    public void BackendDialogRejectsInvalidSelectionAndPersistsOtherPreferences() => OnSta(() =>
    {
        using var fixture = new Fixture();
        var backend = fixture.Controller();
        var saved = new AppSettings { WordWrap = true, ShowTimestamp = true, SendMode = SendMode.Enter };
        backend.Save(saved);
        var dialog = new BackendSettingsWindow(backend);
        var input = (TextBox)dialog.FindName("ExecutableInput");
        input.Text = Path.Combine(fixture.Root, "missing.exe");
        var panel = (Grid)dialog.Content;
        var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("not be silently replaced", ((TextBlock)dialog.FindName("ValidationMessage")).Text);
        Assert.Equal(saved, fixture.Store.Load());
        dialog.Close();
        return Task.CompletedTask;
    });

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Backend dialog/command test timed out.");
        Assert.Null(failure);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "winchat-backend-" + Guid.NewGuid());
        public string Work => Path.Combine(Root, "work");
        public string Executable { get; }
        public SettingsStore Store { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Work);
            Executable = Path.Combine(AppContext.BaseDirectory, "Fake CLI", "WinChatCopilot.FakeCli.exe");
            Store = new SettingsStore(Path.Combine(Root, "settings.json"));
        }
        public BackendController Controller()
        {
            Store.Save(new AppSettings { ExecutablePath = Executable });
            var controller = new BackendController(Store,
                new BackendDiscovery("", []), new CopilotReadiness(Root).PrepareAsync);
            controller.Load();
            return controller;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
