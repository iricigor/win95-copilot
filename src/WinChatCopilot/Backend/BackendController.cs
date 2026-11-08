using WinChatCopilot.Settings;

namespace WinChatCopilot.Backend;

public enum BackendState { Disconnected, Preparing, Ready, Generating, Failed }

public sealed class BackendController(SettingsStore store, BackendDiscovery discovery,
    Func<string, CancellationToken, Task<BackendSession>> prepare)
{
    public AppSettings Settings { get; private set; } = new();
    public BackendState State { get; private set; } = BackendState.Disconnected;
    public BackendSession? Session { get; private set; }
    public bool IsBusy => State is BackendState.Preparing or BackendState.Generating;
    public event Action? Changed;

    public void Load() => Settings = store.Load();
    public string Discover() => discovery.Resolve(Settings.ExecutablePath);

    public void Save(AppSettings settings)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for connection preparation or generation before changing settings.");
        if (settings.ExecutablePath is not null)
            BackendDiscovery.ValidateExplicit(settings.ExecutablePath);
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
        Session = null;
        Changed?.Invoke();
        try
        {
            var executable = Discover();
            Session = await prepare(executable, cancellationToken);
            State = BackendState.Ready;
        }
        finally
        {
            if (Session is null)
                State = cancellationToken.IsCancellationRequested ? BackendState.Disconnected : BackendState.Failed;
            Changed?.Invoke();
        }

    }

    public async Task<string> SendAsync(string prompt, Action<string> onText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Enter a nonempty message before sending.", nameof(prompt));
        if (State != BackendState.Ready || Session is null)
            throw new InvalidOperationException("Copilot is unavailable or busy. Connect before sending.");
        State = BackendState.Generating;
        Changed?.Invoke();
        try
        {
            var text = await new CopilotChat().SendAsync(Session, prompt, onText, cancellationToken);
            State = BackendState.Ready;
            return text;
        }
        finally
        {
            if (State == BackendState.Generating)
            {
                Session = null;
                State = cancellationToken.IsCancellationRequested ? BackendState.Disconnected : BackendState.Failed;
            }
            Changed?.Invoke();
        }
    }
}
