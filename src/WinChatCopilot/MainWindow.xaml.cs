using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinChatCopilot.Backend;
using WinChatCopilot.Settings;

namespace WinChatCopilot;

public partial class MainWindow : RetroWindow
{
    private readonly BackendController backend;
    private readonly CancellationTokenSource lifetime = new();
    private bool closing;
    private bool initialized;
    private Task? pendingConnection;
    private Task? pendingGeneration;
    private readonly List<ChatTurn> turns = [];
    public IReadOnlyList<ChatTurn> Turns => turns.AsReadOnly();

    public MainWindow() : this(CreateBackend()) { }

    public MainWindow(BackendController backend)
    {
        this.backend = backend;
        foreach (var command in ShellCommands.Unimplemented)
            CommandBindings.Add(new CommandBinding(command, null, (_, e) => e.CanExecute = false));
        CommandBindings.Add(new CommandBinding(ShellCommands.About,
            (_, _) => new AboutWindow { Owner = this }.ShowDialog(), (_, e) => e.CanExecute = true));
        CommandBindings.Add(new CommandBinding(ShellCommands.Connect,
            async (_, _) => await ConnectAsync(), (_, e) => e.CanExecute = initialized && !closing &&
                !backend.IsBusy && pendingGeneration is null));
        CommandBindings.Add(new CommandBinding(ShellCommands.ConfigureBackend,
            async (_, _) => await ConfigureAsync(), (_, e) => e.CanExecute = initialized && !closing &&
                !backend.IsBusy && pendingGeneration is null));
        CommandBindings.Add(new CommandBinding(ShellCommands.AutoConnect,
            (_, _) => SaveAutoConnect(), (_, e) => e.CanExecute = initialized && !closing &&
                !backend.IsBusy && pendingGeneration is null));
        CommandBindings.Add(new CommandBinding(ShellCommands.Send,
            async (_, _) => await SendAsync(), (_, e) => e.CanExecute = !closing &&
                pendingGeneration is null && backend.State == BackendState.Ready &&
                !string.IsNullOrWhiteSpace(PromptInput?.Text)));
        InitializeComponent();
        Title = $"WinChat-Copilot {ApplicationInfo.Version}";
        backend.Changed += UpdateBackend;
        Loaded += async (_, _) => await InitializeBackendAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
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
        if (closing || backend.IsBusy) return;
        pendingConnection = backend.ConnectAsync(lifetime.Token);
        try { await pendingConnection; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBackendError(error)) { ShowError(error); }
        finally { pendingConnection = null; }
    }

    private void SaveAutoConnect()
    {
        try { backend.Save(backend.Settings with { AutoConnect = !backend.Settings.AutoConnect }); }
        catch (Exception error) when (IsBackendError(error)) { ShowError(error); }
        UpdateBackend();
    }

    private void OnPromptChanged(object sender, TextChangedEventArgs e) => CommandManager.InvalidateRequerySuggested();

    internal Task SendAsync()
    {
        if (!ShellCommands.Send.CanExecute(null, this)) return Task.CompletedTask;
        var prompt = PromptInput.Text;
        var turn = new ChatTurn(prompt);
        turns.Add(turn);
        Transcript.AppendText($"User:\r\n{prompt}\r\n\r\nAssistant:\r\n");
        var generation = GenerateAsync(turn);
        pendingGeneration = generation.IsCompleted ? null : generation;
        return generation;
    }

    private async Task GenerateAsync(ChatTurn turn)
    {
        Task lastUpdate = Task.CompletedTask;
        Exception? failure = null;
        try
        {
            await backend.SendAsync(turn.Prompt, chunk =>
            {
                lastUpdate = Dispatcher.InvokeAsync(() =>
                {
                    turn.Response += chunk;
                    Transcript.AppendText(chunk);
                }).Task;
            }, lifetime.Token);
            turn.State = ResponseState.Completed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            turn.State = ResponseState.Cancelled;
        }
        catch (Exception error) when (IsBackendError(error))
        {
            turn.State = ResponseState.Failed;
            turn.Error = error.Message;
            failure = error;
        }
        finally
        {
            await lastUpdate;
            turn.FinishedAt = DateTimeOffset.Now;
            Transcript.AppendText(turn.State switch
            {
                ResponseState.Failed => "\r\n[Failed]\r\n\r\n",
                ResponseState.Cancelled => "\r\n[Cancelled]\r\n\r\n",
                _ => "\r\n\r\n"
            });
            if (turn.State == ResponseState.Completed) PromptInput.Clear();
            pendingGeneration = null;
            UpdateBackend();
        }
        if (failure is not null) ShowError(failure);
    }

    private void UpdateBackend()
    {
        AutoConnectMenu.IsChecked = backend.Settings.AutoConnect;
        PromptInput.IsReadOnly = backend.IsBusy || pendingGeneration is not null || closing;
        BackendStatus.Text = backend.State switch
        {
            BackendState.Preparing => "Checking Copilot authentication and text-only readiness...",
            BackendState.Ready => "Copilot ready - enter a message and choose SEND.",
            BackendState.Generating => "Copilot generating - SEND disabled. Exit cancels the owned request.",
            BackendState.Failed => "Backend check failed - correct configuration/authentication, then Connect again.",
            _ => "Backend unavailable - not connected. Use Connect or Backend Settings."
        };
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
        var pending = pendingGeneration ?? pendingConnection;
        if (closing || pending is null) return;
        e.Cancel = true;
        closing = true;
        lifetime.Cancel();
        try { await pending; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBackendError(error))
        {
            closing = false;
            ShowError(error);
            return;
        }
        Close();
    }
}