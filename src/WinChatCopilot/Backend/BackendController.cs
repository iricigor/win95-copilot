using WinChatCopilot.Settings;

namespace WinChatCopilot.Backend;

public enum BackendState { Disconnected, Preparing, Ready, Failed }

public sealed class BackendController(SettingsStore store, BackendDiscovery discovery,
    Func<string, CancellationToken, Task<BackendSession>> prepare)
{
    public AppSettings Settings { get; private set; } = new();
    public BackendState State { get; private set; } = BackendState.Disconnected;
    public BackendSession? Session { get; private set; }
    public bool IsBusy => State == BackendState.Preparing;
    public event Action? Changed;

    public void Load() => Settings = store.Load();
    public string Discover() => discovery.Resolve(Settings.ExecutablePath);

    public void Save(AppSettings settings)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for connection preparation before changing settings.");
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
}
