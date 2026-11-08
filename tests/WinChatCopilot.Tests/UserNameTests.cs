using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot.Tests;

[Collection("WPF")]
public class UserNameTests
{
    [Theory]
    [InlineData("User", "User")]
    [InlineData("  \u6f22 synthetic name \u20ac  ", "\u6f22 synthetic name \u20ac")]
    [InlineData("Assistant", "Assistant")]
    public void ValidNamesTrimOnlyOuterWhitespaceAndPreserveUnicode(string input, string expected) =>
        Assert.Equal(expected, UserDisplayName.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\rbreak")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tname")]
    [InlineData("\tname")]
    [InlineData("name\0")]
    [InlineData("name\u001b")]
    [InlineData("name\u007f")]
    [InlineData("name\u0085")]
    [InlineData("name\u2028break")]
    [InlineData("name\u2029break")]
    public void InvalidNamesHaveActionableGuidance(string? input) =>
        Assert.Contains("user name", Assert.Throws<InvalidDataException>(() => UserDisplayName.Normalize(input)).Message);

    [Fact]
    public void SettingsAreBackwardCompatibleAndPreserveNamesAcrossUnrelatedSaves()
    {
        using var fixture = new Fixture();
        Assert.Equal("User", fixture.Store.Load().UserName);
        File.WriteAllText(fixture.Store.FilePath, "{\"SchemaVersion\":1,\"WordWrap\":true}");
        Assert.Equal("User", fixture.Store.Load().UserName);
        var settings = fixture.Store.Load() with { UserName = "\u6f22 synthetic name \u20ac" };
        fixture.Store.Save(settings);
        foreach (var changed in new[]
        {
            settings with { ShowTimestamp = true },
            settings with { WordWrap = false },
            settings with { SendMode = SendMode.Enter },
            settings with { AutoConnect = true },
            settings with { ExecutablePath = fixture.Executable }
        })
        {
            fixture.Store.Save(changed);
            Assert.Equal(changed, fixture.Store.Load());
            Assert.Equal(settings.UserName, fixture.Store.Load().UserName);
        }
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(settings with { UserName = "" }));
        Assert.Equal(settings.UserName, fixture.Store.Load().UserName);
    }

    [Theory]
    [InlineData("{\"UserName\":null}")]
    [InlineData("{\"UserName\":\" \"}")]
    [InlineData("{\"UserName\":\"a\\nb\"}")]
    [InlineData("{\"UserName\":\"a\\tb\"}")]
    [InlineData("{\"UserName\":\"a\\u0000b\"}")]
    public void InvalidStoredNamesAreNotSilentlyRewritten(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.FilePath, json);
        Assert.Contains("Cannot read settings", Assert.Throws<IOException>(() => fixture.Store.Load()).Message);
        Assert.Equal(json, File.ReadAllText(fixture.Store.FilePath));
    }

    [Theory]
    [InlineData(ResponseState.Completed)]
    [InlineData(ResponseState.Failed)]
    [InlineData(ResponseState.Cancelled)]
    [InlineData(ResponseState.Generating)]
    public void ExactDisplayAndExportUseCurrentNameButKeepAssistantRoleAndCanonicalData(ResponseState state) => OnSta(() =>
    {
        var turn = new ChatTurn("prompt\n\u6f22", new DateTimeOffset(2026, 11, 8, 9, 5, 47, TimeSpan.FromHours(5)))
            { Response = "reply\n\u20ac", State = state };
        var view = new TranscriptView();
        foreach (var name in new[] { "\u6f22 synthetic name", "Assistant" })
        foreach (var timestamps in new[] { false, true })
        {
            var prefix = timestamps ? "[09:05] " : "";
            view.Update([turn], timestamps, userName: name);
            Assert.Equal(TranscriptDisplay.Format([turn], timestamps, userName: name), view.Text);
            Assert.StartsWith(prefix + name + ": prompt\n\u6f22\r\n" + prefix + "Assistant: reply\n\u20ac", view.Text);
            var exported = TranscriptDisplay.Format([turn], timestamps, markGenerating: true, userName: name);
            var marker = TranscriptDisplay.Marker(state, markGenerating: true);
            Assert.Equal(view.Text.TrimEnd('\r', '\n') +
                (state == ResponseState.Generating ? "\r\n[In progress]" : "") + "\r\n", exported);
            if (marker.Length != 0) Assert.Contains(marker, exported);
            Assert.Equal(name, ((Run)((Paragraph)view.Document.Blocks.FirstBlock).Inlines.ElementAt(1)).Text);
            Assert.Equal("Assistant", ((Run)((Paragraph)view.Document.Blocks.LastBlock).Inlines.ElementAt(1)).Text);
        }
        Assert.Equal("prompt\n\u6f22", turn.Prompt);
        Assert.Equal("reply\n\u20ac", turn.Response);
        Assert.Equal(state, turn.State);
        return Task.CompletedTask;
    });

    [Fact]
    public void VeryLongValidNameKeepsTextAndNativeWidthLimits() => OnSta(() =>
    {
        var name = new string('W', 130_000);
        Assert.Equal(name, UserDisplayName.Normalize(name));
        using var fixture = new Fixture();
        fixture.Store.Save(new AppSettings { UserName = name });
        Assert.Equal(name, fixture.Store.Load().UserName);
        var view = new TranscriptView();
        var turn = new ChatTurn("synthetic") { Response = "reply", State = ResponseState.Completed };
        view.Update([turn], false, userName: name);
        Assert.Equal(TranscriptDisplay.Format([turn], false, userName: name), view.Text);
        Assert.InRange(view.Document.PageWidth, 1, 1_000_000);
        view.SetWordWrap(true);
        Assert.True(double.IsNaN(view.Document.PageWidth));
        Assert.Equal(name.Length, ((Run)((Paragraph)view.Document.Blocks.FirstBlock).Inlines.ElementAt(1)).Text.Length);
        return Task.CompletedTask;
    });

    [Fact]
    public void OptionsDialogValidatesSavesAndRestoresAcrossRestart() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        var observed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = window.OwnedWindows.OfType<UserNameWindow>().SingleOrDefault();
            if (dialog is null) return;
            timer.Stop();
            observed = true;
            var input = (TextBox)dialog.FindName("NameInput");
            var ok = (Button)dialog.FindName("OkButton");
            Assert.Equal("User", input.Text);
            Assert.True(ok.IsDefault);
            Assert.True(((Button)dialog.FindName("CancelButton")).IsCancel);
            Assert.False(input.AcceptsReturn);
            Assert.False(input.AcceptsTab);
            foreach (var invalid in new[] { " ", "a\nb", "a\tb", "a\0b" })
            {
                input.Text = invalid;
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(dialog.IsVisible);
                Assert.Null(dialog.DialogResult);
                Assert.NotEmpty(((TextBlock)dialog.FindName("ValidationMessage")).Text);
                Assert.Equal("User", backend.Settings.UserName);
                Assert.Equal("User", fixture.Store.Load().UserName);
            }
            input.Text = "  \u6f22 synthetic name  ";
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        try
        {
            Assert.False(ShellCommands.UserName.CanExecute(null, window));
            await window.InitializeBackendAsync();
            window.Show();
            var options = (MenuItem)((Menu)window.FindName("MainMenu")).Items[2];
            var menu = options.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.Command, ShellCommands.UserName));
            Assert.Equal("_User Name...", menu.Header);
            Assert.Same(window, menu.CommandTarget);
            Assert.True(ShellCommands.UserName.CanExecute(null, window));
            timer.Start();
            ShellCommands.UserName.Execute(null, window);
            Assert.True(observed);
            Assert.Equal("\u6f22 synthetic name", backend.Settings.UserName);
            Assert.Equal(backend.Settings, fixture.Store.Load());
            var restartedBackend = fixture.Controller();
            var restarted = new MainWindow(restartedBackend);
            try
            {
                await restarted.InitializeBackendAsync();
                Assert.Equal("\u6f22 synthetic name", restartedBackend.Settings.UserName);
            }
            finally { restarted.Close(); }
        }
        finally { timer.Stop(); window.Close(); }
    });

    [Fact]
    public void DialogCancellationAndLockedSaveKeepPreviousNameAndOtherPreferences() => OnSta(async () =>
    {
        using var fixture = new Fixture();
        var backend = fixture.Controller();
        var window = new MainWindow(backend);
        try
        {
            await window.InitializeBackendAsync();
            backend.Save(backend.Settings with { WordWrap = true, ShowTimestamp = true, SendMode = SendMode.Enter });
            var previous = backend.Settings;
            var originalFile = File.ReadAllText(fixture.Store.FilePath);
            window.Show();
            foreach (var lockedSave in new[] { false, true })
            {
                using var locked = lockedSave ? File.Open(fixture.Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
                var dialog = new UserNameWindow(previous.UserName, window.SaveUserName) { Owner = window };
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    ((TextBox)dialog.FindName("NameInput")).Text = "new synthetic name";
                    if (lockedSave)
                    {
                        ((Button)dialog.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.True(dialog.IsVisible);
                        Assert.Null(dialog.DialogResult);
                        Assert.Contains("Cannot save settings", ((TextBlock)dialog.FindName("ValidationMessage")).Text);
                    }
                    dialog.DialogResult = false;
                };
                timer.Start();
                try { Assert.False(dialog.ShowDialog()); }
                finally { timer.Stop(); dialog.Close(); }
                Assert.Equal(previous, backend.Settings);
            }
            Assert.Equal(originalFile, File.ReadAllText(fixture.Store.FilePath));
            Assert.Equal(previous, fixture.Store.Load());
            window.SaveUserName("  renamed  ");
            Assert.Equal(previous with { UserName = "renamed" }, backend.Settings);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenameDuringStreamingKeepsSelectionFocusScrollSessionAndPrompt(bool followAgain) => OnSta(async () =>
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
            var view = (TranscriptView)window.FindName("Transcript");
            input.Text = "first synthetic prompt";
            var first = window.SendAsync();
            await WaitAsync(() => view.Text.Contains("synthetic line 149"));
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            await first;
            File.Delete(Path.Combine(fixture.Work, "chat.continue"));
            input.Text = "second synthetic prompt";
            var generation = window.SendAsync();
            await WaitAsync(() => window.Turns.Count == 2 && window.Turns[1].Response.Contains("synthetic line 149"));
            view.ScrollToVerticalOffset(100);
            await WaitAsync(() => Math.Abs(view.VerticalOffset - 100) < 1);
            var body = (Run)((Paragraph)view.Document.Blocks.FirstBlock).Inlines.LastInline;
            var start = body.ContentStart.GetPositionAtOffset(1)!;
            view.Selection.Select(start, start.GetPositionAtOffset(5)!);
            input.Focus();
            var selected = view.Selection.Text;
            var offset = view.VerticalOffset;
            var canonical = window.Turns.Select(turn => (turn.Prompt, turn.Response, turn.SubmittedAt, turn.State)).ToArray();
            var session = backend.Session;
            window.SaveUserName("  \u6f22 synthetic name  ");
            ShellCommands.ShowTimestamp.Execute(null, window);
            Assert.Equal(selected, view.Selection.Text);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.InRange(Math.Abs(view.VerticalOffset - offset), 0, 1);
            var paragraphs = view.Document.Blocks.Cast<Paragraph>().ToArray();
            for (var i = 0; i < paragraphs.Length; i++)
                Assert.Equal(i % 2 == 0 ? "\u6f22 synthetic name" : "Assistant",
                    ((Run)paragraphs[i].Inlines.ElementAt(1)).Text);
            Assert.Equal(canonical, window.Turns.Select(turn => (turn.Prompt, turn.Response, turn.SubmittedAt, turn.State)).ToArray());
            Assert.Same(session, backend.Session);
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
            Assert.Equal(TranscriptDisplay.Format(window.Turns, true, userName: "\u6f22 synthetic name"), view.Text);
            var requests = File.ReadAllLines(Path.Combine(fixture.Work, "chat.requests.jsonl"))
                .Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal("first synthetic prompt", requests[0][^1]);
            Assert.Equal("second synthetic prompt", requests[1][^1]);
            Assert.Equal(requests[0].Single(arg => arg.StartsWith("--session-id=")),
                requests[1].Single(arg => arg.StartsWith("--session-id=")));
            Assert.Same(session, backend.Session);
        }
        finally { await window.StopAsync(); window.Close(); }
    });

    [Fact]
    public void NameChangeInsideExportPathDialogCannotChangeCapturedSnapshot() => OnSta(async () =>
    {
        using var fixture = new Fixture("stream");
        var backend = fixture.Controller();
        var destination = Path.Combine(fixture.Root, "export.txt");
        var window = new MainWindow(backend, owner =>
        {
            ((MainWindow)owner).SaveUserName("new synthetic name");
            ShellCommands.ShowTimestamp.Execute(null, owner);
            return destination;
        });
        try
        {
            await window.InitializeBackendAsync();
            await backend.ConnectAsync(CancellationToken.None);
            window.SaveUserName("old synthetic name");
            ((TextBox)window.FindName("PromptInput")).Text = "synthetic prompt";
            var generation = window.SendAsync();
            await WaitAsync(() => window.Turns[0].Response == "first ");
            await window.SaveTranscriptAsync();
            Assert.Equal("old synthetic name: synthetic prompt\r\nAssistant: first \r\n[In progress]\r\n",
                File.ReadAllText(destination));
            Assert.Contains("new synthetic name: synthetic prompt", ((TranscriptView)window.FindName("Transcript")).Text);
            File.WriteAllText(Path.Combine(fixture.Work, "chat.continue"), "");
            await generation;
            Assert.Equal("new synthetic name", fixture.Store.Load().UserName);
        }
        finally { await window.StopAsync(); window.Close(); }
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Display-name readiness gate timed out.");
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Display-name test timed out.");
        Assert.Null(failure);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "winchat-name-" + Guid.NewGuid());
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
