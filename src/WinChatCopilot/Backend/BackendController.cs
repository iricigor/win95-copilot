using WinChatCopilot.Settings;

namespace WinChatCopilot.Backend;

public enum BackendState { Disconnected, Preparing, Ready, Generating, Cancelling, Failed }

public sealed class BackendController(SettingsStore store, BackendDiscovery discovery,
    Func<string, CancellationToken, Task<BackendSession>> prepare,
    Func<BackendSession, CancellationToken, Task<BackendSession>>? resume = null)
{
    private CancellationTokenSource? generationCancellation;
    public AppSettings Settings { get; private set; } = new();
    public BackendState State { get; private set; } = BackendState.Disconnected;
    public BackendSession? Session { get; private set; }
    public bool IsBusy => State is BackendState.Preparing or BackendState.Generating or BackendState.Cancelling;
    public event Action? Changed;

    public void Load() => Settings = store.Load();
    public string Discover() => discovery.Resolve(Settings.ExecutablePath);

    public void Save(AppSettings settings)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for connection preparation or generation before changing settings.");
        if (settings.ExecutablePath is not null)
            BackendDiscovery.ValidateExplicit(settings.ExecutablePath);
        if (Session is not null && settings.ExecutablePath != Settings.ExecutablePath)
            throw new InvalidOperationException("Changing the backend would discard this conversation's context. Restart the app to select a different backend.");
        store.Save(settings);
        if (settings.ExecutablePath != Settings.ExecutablePath)
        {
            Session = null;
            State = BackendState.Disconnected;
        }
        Settings = settings;
        Changed?.Invoke();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) throw new InvalidOperationException("A connection check is already running.");
        State = BackendState.Preparing;
        var previousSession = Session;
        Changed?.Invoke();
        try
        {
            var executable = Discover();
            Session = previousSession is null
                ? await prepare(executable, cancellationToken)
                : await (resume ?? CopilotReadiness.ResumeAsync)(previousSession, cancellationToken);
            if (previousSession is not null && Session.Id != previousSession.Id)
            {
                Session = previousSession;
                throw new InvalidOperationException("Recovery changed the conversation identity. No new session was accepted.");
            }
            State = BackendState.Ready;
        }
        finally
        {
            if (State == BackendState.Preparing)
                State = cancellationToken.IsCancellationRequested ? BackendState.Disconnected : BackendState.Failed;
            Changed?.Invoke();
        }

    }

    public void CancelGeneration()
    {
        if (State != BackendState.Generating || generationCancellation is null) return;
        State = BackendState.Cancelling;
        Changed?.Invoke();
        generationCancellation.Cancel();
    }

    public async Task<string> SendAsync(string prompt, Action<string> onText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Enter a nonempty message before sending.", nameof(prompt));
        if (State != BackendState.Ready || Session is null)
            throw new InvalidOperationException("Copilot is unavailable or busy. Connect before sending.");
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        generationCancellation = request;
        State = BackendState.Generating;
        Changed?.Invoke();
        try
        {
            var text = await new CopilotChat().SendAsync(Session, prompt, chunk =>
            {
                if (ReferenceEquals(Volatile.Read(ref generationCancellation), request))
                    onText(chunk);
            }, request.Token);
            State = BackendState.Ready;
            return text;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            State = cancellationToken.IsCancellationRequested ? BackendState.Disconnected : BackendState.Ready;
            throw;
        }
        finally
        {
            generationCancellation = null;
            if (State is BackendState.Generating or BackendState.Cancelling)
                State = BackendState.Failed;
            Changed?.Invoke();
        }
    }
}
