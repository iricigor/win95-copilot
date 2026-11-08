using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot;

public partial class MainWindow : RetroWindow
{
    private readonly BackendController backend;
    private readonly Func<Window, string?> selectTranscriptPath;
    private readonly Func<string, bool> isFontInstalled;
    private readonly Dictionary<string, MenuItem> fontItems = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool closing;
    private bool shutdownComplete;
    private ChatTurn? activeTurn;
    private bool stopRequested;
    private bool initialized;
    private Task? pendingConnection;
    private Task? pendingGeneration;
    private Task? pendingReset;
    private Task? pendingExport;
    private bool savingTranscript;
    private bool resetting;
    private bool followingTranscript = true;
    private bool updatingTranscript;
    private DispatcherTimer? waitingTimer;
    internal int WaitingDots { get; private set; }
    internal bool IsWaitingForText => waitingTimer?.IsEnabled == true;
    private readonly List<ChatTurn> turns = [];
    public IReadOnlyList<ChatTurn> Turns => turns.AsReadOnly();

    public MainWindow() : this(CreateBackend()) { }

    public MainWindow(BackendController backend) : this(backend, TranscriptExport.SelectPath) { }

    internal MainWindow(BackendController backend, Func<Window, string?> selectTranscriptPath)
        : this(backend, selectTranscriptPath, TranscriptFont.IsInstalled) { }

    internal MainWindow(BackendController backend, Func<Window, string?> selectTranscriptPath,
        Func<string, bool> isFontInstalled)
    {
        this.backend = backend;
        this.selectTranscriptPath = selectTranscriptPath;
        this.isFontInstalled = isFontInstalled;
        foreach (var command in ShellCommands.Unimplemented)
            CommandBindings.Add(new CommandBinding(command, null, (_, e) => e.CanExecute = false));
        CommandBindings.Add(new CommandBinding(ShellCommands.About,
            (_, _) => new AboutWindow { Owner = this }.ShowDialog(), (_, e) => e.CanExecute = true));
        CommandBindings.Add(new CommandBinding(ShellCommands.Connect,
            async (_, _) => await ConnectAsync(), (_, e) => e.CanExecute = initialized && !closing && !resetting &&
                !backend.IsBusy && pendingGeneration is null));
        CommandBindings.Add(new CommandBinding(ShellCommands.ConfigureBackend,
            async (_, _) => await ConfigureAsync(), (_, e) => e.CanExecute = initialized && !closing && !resetting &&
                !backend.IsBusy && pendingGeneration is null));
        CommandBindings.Add(new CommandBinding(ShellCommands.AutoConnect,
            (_, _) => SaveAutoConnect(), (_, e) => e.CanExecute = initialized && !closing && !resetting &&
                !backend.IsBusy && pendingGeneration is null));
        CommandBindings.Add(new CommandBinding(ShellCommands.SendWithEnter,
            (_, _) => SaveDisplaySettings(backend.Settings with { SendMode = SendMode.Enter }),
            (_, e) => e.CanExecute = initialized && !closing));
        CommandBindings.Add(new CommandBinding(ShellCommands.SendWithControlEnter,
            (_, _) => SaveDisplaySettings(backend.Settings with { SendMode = SendMode.ControlEnter }),
            (_, e) => e.CanExecute = initialized && !closing));
        CommandBindings.Add(new CommandBinding(ShellCommands.WordWrap,
            (_, _) => SaveDisplaySettings(backend.Settings with { WordWrap = !backend.Settings.WordWrap }),
            (_, e) => e.CanExecute = initialized && !closing));
        CommandBindings.Add(new CommandBinding(ShellCommands.ShowTimestamp,
            (_, _) => SaveDisplaySettings(backend.Settings with { ShowTimestamp = !backend.Settings.ShowTimestamp }),
            (_, e) => e.CanExecute = initialized && !closing));
        CommandBindings.Add(new CommandBinding(ShellCommands.UserName,
            (_, _) => new UserNameWindow(backend.Settings.UserName, SaveUserName) { Owner = this }.ShowDialog(),
            (_, e) => e.CanExecute = initialized && !closing));
        CommandBindings.Add(new CommandBinding(ShellCommands.SelectFont,
            (_, e) => SaveDisplaySettings(backend.Settings with { TextFont = (string)e.Parameter }),
            (_, e) => e.CanExecute = initialized && !closing && e.Parameter is string font &&
                TranscriptFont.Choices.Contains(font) && isFontInstalled(font)));
        CommandBindings.Add(new CommandBinding(ShellCommands.Send,
            async (_, _) => await SendAsync(), (_, e) => e.CanExecute = !closing && !resetting &&
                pendingGeneration is null && backend.State == BackendState.Ready &&
                !string.IsNullOrWhiteSpace(PromptInput?.Text)));
        CommandBindings.Add(new CommandBinding(ShellCommands.Stop,
            async (_, _) => await StopAsync(), (_, e) => e.CanExecute = !closing && !resetting &&
                pendingGeneration is not null && backend.State == BackendState.Generating));
        CommandBindings.Add(new CommandBinding(ShellCommands.ClearWindow,
            async (_, _) => await ClearWindowAsync(), (_, e) => e.CanExecute = initialized && !closing &&
                !resetting && pendingConnection is null && backend.State is not BackendState.Preparing));
        CommandBindings.Add(new CommandBinding(ShellCommands.SaveTranscript,
            async (_, _) => await SaveTranscriptAsync(), (_, e) => e.CanExecute = initialized && !closing &&
                !resetting && !savingTranscript));
        InitializeComponent();
        foreach (var font in TranscriptFont.Choices)
        {
            var installed = isFontInstalled(font);
            var item = new MenuItem
            {
                Header = new TextBlock
                {
                    Text = installed ? font : $"{font} (not installed)",
                    FontFamily = installed ? new FontFamily(font) : MainMenu.FontFamily
                },
                IsCheckable = true,
                IsEnabled = installed,
                Command = ShellCommands.SelectFont,
                CommandParameter = font,
                CommandTarget = this,
                ToolTip = installed ? $"Use {font} for transcript and message input."
                    : $"{font} is not installed. Choose another available font."
            };
            fontItems.Add(font, item);
            FontMenu.Items.Add(item);
        }
        Transcript.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnTranscriptScrollChanged));
        Title = $"WinChat-Copilot {ApplicationInfo.Version}";
        backend.Changed += UpdateBackend;
        Loaded += async (_, _) => await InitializeBackendAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            StopWaitingIndicator(updateHeading: false);
            backend.Changed -= UpdateBackend;
            lifetime.Dispose();
        };
    }

    private static BackendController CreateBackend()
    {
        var root = Environment.GetEnvironmentVariable("WINCHAT_DATA_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinChatCopilot");
        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var readiness = new CopilotReadiness(store.DirectoryPath);
        return new BackendController(store, BackendDiscovery.CreateDefault(), readiness.PrepareAsync);
    }

    internal async Task InitializeBackendAsync()
    {
        if (initialized) return;
        initialized = true;
        try { backend.Load(); }
        catch (IOException error)
        {
            ShowError(error);
            UpdateBackend();
            await ConfigureAsync(error.Message, connectAfterSave: false);
            return;
        }
        UpdateBackend();
        try { backend.Discover(); }
        catch (Exception error) when (IsBackendError(error))
        {
            await ConfigureAsync(error.Message);
            return;
        }
        if (backend.Settings.AutoConnect) await ConnectAsync();
    }

    private async Task ConfigureAsync(string? guidance = null, bool connectAfterSave = true)
    {
        var dialog = new BackendSettingsWindow(backend, guidance) { Owner = this };
        if (dialog.ShowDialog() == true && connectAfterSave && backend.Settings.AutoConnect)
            await ConnectAsync();
        UpdateBackend();
    }

    private async Task ConnectAsync()
    {
        if (closing || resetting || backend.IsBusy) return;
        pendingConnection = backend.ConnectAsync(lifetime.Token);
        try { await pendingConnection; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBackendError(error)) { ShowError(error); }
        finally { pendingConnection = null; UpdateBackend(); }
    }

    private void SaveAutoConnect()
    {
        try { backend.Save(backend.Settings with { AutoConnect = !backend.Settings.AutoConnect }); }
        catch (Exception error) when (IsBackendError(error)) { ShowError(error); }
        UpdateBackend();
    }

    private void OnPromptChanged(object sender, TextChangedEventArgs e) => CommandManager.InvalidateRequerySuggested();

    private void OnPromptKeyDown(object sender, KeyEventArgs e) =>
        e.Handled = HandlePromptKey(e.Key, Keyboard.Modifiers);

    internal bool HandlePromptKey(Key key, ModifierKeys modifiers)
    {
        if (key != Key.Enter || (backend.Settings.SendMode != SendMode.Enter && modifiers != ModifierKeys.Control))
            return false;
        if (ShellCommands.Send.CanExecute(null, this)) ShellCommands.Send.Execute(null, this);
        return true;
    }

    private void SaveDisplaySettings(AppSettings settings)
    {
        var timestampsChanged = settings.ShowTimestamp != backend.Settings.ShowTimestamp;
        try
        {
            backend.Save(settings);
            if (timestampsChanged) RefreshTranscript();
        }
        catch (Exception error) when (IsBackendError(error)) { ShowError(error); }
        UpdateBackend();
    }

    internal void SaveUserName(string name)
    {
        backend.Save(backend.Settings with { UserName = UserDisplayName.Normalize(name) });
        RefreshTranscript();
        UpdateBackend();
    }

    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (updatingTranscript) return;
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
            followingTranscript = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 0.5;
        if (followingTranscript && e.ExtentHeightChange != 0)
            Transcript.ScrollToVerticalOffset(double.MaxValue);
    }

    private void ChangeTranscript(Action change)
    {
        var offset = Transcript.VerticalOffset;
        var horizontalOffset = Transcript.HorizontalOffset;
        var selectionEmpty = Transcript.Selection.IsEmpty;
        var selectionStart = Transcript.Selection.Start.GetPositionAtOffset(0, System.Windows.Documents.LogicalDirection.Forward)!;
        var selectionEnd = Transcript.Selection.End.GetPositionAtOffset(0, System.Windows.Documents.LogicalDirection.Backward)!;
        updatingTranscript = true;
        try
        {
            change();
            Transcript.Selection.Select(selectionStart, selectionEmpty ? selectionStart : selectionEnd);
            Transcript.UpdateLayout();
            Transcript.ScrollToVerticalOffset(followingTranscript ? double.MaxValue : offset);
            Transcript.ScrollToHorizontalOffset(horizontalOffset);
        }
        finally { updatingTranscript = false; }
    }

    private void RefreshTranscript() =>
        ChangeTranscript(() => Transcript.Update(turns, backend.Settings.ShowTimestamp, WaitingDots, backend.Settings.UserName));

    private void StartWaitingIndicator()
    {
        WaitingDots = 1;
        waitingTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        waitingTimer.Tick += OnWaitingTick;
        waitingTimer.Start();
    }

    private void OnWaitingTick(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, waitingTimer) && activeTurn is not null)
            AdvanceWaitingIndicator(activeTurn);
    }

    internal void AdvanceWaitingIndicator(ChatTurn turn)
    {
        if (!ReferenceEquals(activeTurn, turn) || !IsWaitingForText || closing || resetting) return;
        WaitingDots = WaitingDots % 5 + 1;
        ChangeTranscript(() => Transcript.UpdateWaitingIndicator(WaitingDots));
    }

    private void StopWaitingIndicator(bool updateHeading = true)
    {
        if (waitingTimer is null) return;
        waitingTimer.Stop();
        waitingTimer.Tick -= OnWaitingTick;
        waitingTimer = null;
        WaitingDots = 0;
        if (updateHeading)
            ChangeTranscript(() => Transcript.UpdateWaitingIndicator(0));
    }

    internal Task SaveTranscriptAsync()
    {
        if (!ShellCommands.SaveTranscript.CanExecute(null, this)) return Task.CompletedTask;
        var snapshot = TranscriptDisplay.Format(turns, backend.Settings.ShowTimestamp, markGenerating: true,
            userName: backend.Settings.UserName);
        savingTranscript = true;
        UpdateBackend();
        return pendingExport = ExportAsync(snapshot);
    }

    private async Task ExportAsync(string snapshot)
    {
        await System.Windows.Threading.Dispatcher.Yield();
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var path = selectTranscriptPath(this);
            if (path is not null) await TranscriptExport.WriteAsync(path, snapshot, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBackendError(error)) { ShowError(error); }
        finally
        {
            savingTranscript = false;
            pendingExport = null;
            UpdateBackend();
        }
    }

    internal Task SendAsync()
    {
        if (!ShellCommands.Send.CanExecute(null, this)) return Task.CompletedTask;
        var prompt = PromptInput.Text;
        var turn = new ChatTurn(prompt);
        stopRequested = false;
        activeTurn = turn;
        turns.Add(turn);
        StartWaitingIndicator();
        RefreshTranscript();
        var generation = GenerateAsync(turn);
        pendingGeneration = generation.IsCompleted ? null : generation;
        return generation;
    }

    internal async Task StopAsync()
    {
        var pending = pendingGeneration;
        if (pending is null) return;
        stopRequested = true;
        StopWaitingIndicator();
        backend.CancelGeneration();
        await pending;
    }

    internal Task ClearWindowAsync()
    {
        if (!ShellCommands.ClearWindow.CanExecute(null, this)) return Task.CompletedTask;
        resetting = true;
        UpdateBackend();
        var reset = ResetConversationAsync();
        pendingReset = reset.IsCompleted ? null : reset;
        return reset;
    }

    private async Task ResetConversationAsync()
    {
        try
        {
            await StopAsync();
            lifetime.Token.ThrowIfCancellationRequested();
            activeTurn = null;
            turns.Clear();
            followingTranscript = true;
            RefreshTranscript();
            await backend.ResetAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBackendError(error)) { ShowError(error); }
        finally
        {
            resetting = false;
            pendingReset = null;
            UpdateBackend();
        }
    }

    private async Task GenerateAsync(ChatTurn turn)
    {
        Task lastUpdate = Task.CompletedTask;
        Exception? failure = null;
        var finalState = ResponseState.Completed;
        try
        {
            await backend.SendAsync(turn.Prompt, chunk =>
            {
                lastUpdate = Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(activeTurn, turn) || shutdownComplete) return;
                    if (chunk.Length != 0) StopWaitingIndicator(updateHeading: false);
                    turn.Response += chunk;
                    RefreshTranscript();
                }).Task;
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (stopRequested || lifetime.IsCancellationRequested)
        {
            finalState = ResponseState.Cancelled;
        }
        catch (Exception error) when (IsBackendError(error))
        {
            finalState = ResponseState.Failed;
            turn.Error = error.Message;
            failure = error;
        }
        finally
        {
            await lastUpdate;
            StopWaitingIndicator(updateHeading: false);
            turn.State = finalState;
            turn.FinishedAt = DateTimeOffset.Now;
            RefreshTranscript();
            if (turn.State == ResponseState.Completed) PromptInput.Clear();
            activeTurn = null;
            pendingGeneration = null;
            UpdateBackend();
        }
        if (failure is not null) ShowError(failure);
    }

    private void UpdateBackend()
    {
        var fontAvailable = isFontInstalled(backend.Settings.TextFont);
        var effectiveFont = fontAvailable ? backend.Settings.TextFont : TranscriptFont.Default;
        foreach (var (font, item) in fontItems)
            item.IsChecked = fontAvailable && font == backend.Settings.TextFont;
        FontStatus.Text = fontAvailable ? "" :
            $"Saved font '{backend.Settings.TextFont}' is not installed. Temporarily rendering with Courier New" +
            (isFontInstalled(TranscriptFont.Default) ? ". " : " (also unavailable; Windows supplies a substitute). ") +
            "The saved preference is unchanged; choose an installed font in Options > Font.";
        FontStatus.Visibility = fontAvailable ? Visibility.Collapsed : Visibility.Visible;
        if (Transcript.FontFamily.Source != effectiveFont || Transcript.Document.FontFamily.Source != effectiveFont)
            ChangeTranscript(() => Transcript.SetTextFont(new FontFamily(effectiveFont)));
        if (PromptInput.FontFamily.Source != effectiveFont)
            PromptInput.FontFamily = new FontFamily(effectiveFont);
        AutoConnectMenu.IsChecked = backend.Settings.AutoConnect;
        SendEnterMenu.IsChecked = backend.Settings.SendMode == SendMode.Enter;
        SendControlEnterMenu.IsChecked = backend.Settings.SendMode == SendMode.ControlEnter;
        WordWrapMenu.IsChecked = backend.Settings.WordWrap;
        TimestampMenu.IsChecked = backend.Settings.ShowTimestamp;
        PromptInput.AcceptsReturn = backend.Settings.SendMode == SendMode.ControlEnter;
        var wrapping = backend.Settings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PromptInput.TextWrapping = wrapping;
        Transcript.SetWordWrap(backend.Settings.WordWrap);
        Transcript.HorizontalScrollBarVisibility = PromptInput.HorizontalScrollBarVisibility =
            backend.Settings.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PromptInput.IsReadOnly = backend.IsBusy || pendingGeneration is not null || resetting || closing;
        BackendStatus.Text = backend.State switch
        {
            BackendState.Preparing => "Checking Copilot authentication and text-only readiness...",
            BackendState.Ready => "Copilot ready - enter a message and choose SEND.",
            BackendState.Generating => "Copilot generating - choose Stop to cancel the owned request.",
            BackendState.Cancelling => "Cancelling Copilot - waiting for owned work to settle. SEND disabled.",
            BackendState.Failed => backend.Session is null
                ? "Copilot unavailable - new conversation preparation failed. Connect to prepare a new session; no old context or automatic retry."
                : "Copilot failed - Connect checks the same session; no automatic retry. Review retained input before SEND.",
            _ => "Backend unavailable - not connected. Use Connect or Backend Settings."
        };
        if (closing) BackendStatus.Text = "Closing - waiting for owned Copilot work to settle...";
        else if (resetting) BackendStatus.Text = "Clearing Window - settling old work and preparing a new conversation. SEND disabled.";
        else if (backend.State == BackendState.Ready && turns.LastOrDefault()?.State == ResponseState.Cancelled)
            BackendStatus.Text = "Request cancelled; partial text kept. Same session retained; review input before SEND (not retried).";
        CommandManager.InvalidateRequerySuggested();
    }

    private void ShowError(Exception error)
    {
        if (!closing) new ErrorWindow(error.Message) { Owner = this }.ShowDialog();
    }

    private static bool IsBackendError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or Win32Exception or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete) return;
        StopWaitingIndicator();
        if (!closing && pendingGeneration is null && pendingConnection is null && pendingReset is null && pendingExport is null)
        {
            closing = true;
            shutdownComplete = true;
            lifetime.Cancel();
            return;
        }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        UpdateBackend();
        lifetime.Cancel();
        try { await Task.WhenAll(new[] { pendingGeneration, pendingConnection, pendingReset, pendingExport }.OfType<Task>()); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBackendError(error))
        {
            closing = false;
            UpdateBackend();
            ShowError(error);
            return;
        }
        await System.Windows.Threading.Dispatcher.Yield();
        shutdownComplete = true;
        Close();
    }
}