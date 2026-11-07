using System.Reflection;

namespace WinChatCopilot;

public static class ApplicationInfo
{
    public static string Version { get; } = typeof(ApplicationInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0]
        ?? throw new InvalidOperationException("Application version metadata is missing.");
}
