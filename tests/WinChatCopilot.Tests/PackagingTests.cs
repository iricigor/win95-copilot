using System.Diagnostics;
using System.IO;
using System.Text;
using WinChatCopilot.Processes;

namespace WinChatCopilot.Tests;

public class PackagingTests
{
    [Theory]
    [InlineData("Test-RealCopilot.ps1")]
    [InlineData("Test-PackagedApp.ps1")]
    public async Task IntegrationScriptsRefuseUnapprovedRunsBeforeFilesystemOrServiceAccess(string script)
    {
        var directory = Path.Combine(Path.GetTempPath(), "winchat-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var arguments = script == "Test-PackagedApp.ps1"
                ? new[] { "-ExecutablePath", "missing.exe", "-FakeCliPath", "missing-helper.exe", "-EvidenceDirectory", directory }
                : [];
            var result = await RunScript(script, arguments);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains(script == "Test-PackagedApp.ps1" ? "explicit" : "opt-in", result.Error);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task PublishingRejectsRelativeOrExistingOutputWithoutInvokingDotnet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "winchat-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sentinel = Path.Combine(directory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve synthetic existing output");
        try
        {
            var relative = await RunScript("Publish-App.ps1",
                ["-OutputDirectory", "relative-output", "-DotnetPath", "missing-dotnet.exe"]);
            Assert.NotEqual(0, relative.Exit);
            Assert.Contains("absolute", relative.Error);
            var existing = await RunScript("Publish-App.ps1",
                ["-OutputDirectory", directory, "-DotnetPath", "missing-dotnet.exe"]);
            Assert.NotEqual(0, existing.Exit);
            Assert.Contains("already exists", existing.Error);
            Assert.Equal("preserve synthetic existing output", await File.ReadAllTextAsync(sentinel));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { File.Delete(sentinel); Directory.Delete(directory); }
    }

    private static async Task<(int Exit, string Error)> RunScript(string script, string[] arguments)
    {
        var powershell = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe")
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            "Packaging script regressions require PowerShell 7 in its standard installation or app-execution-alias location.");
        var info = new ProcessStartInfo(powershell) { WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(AppContext.BaseDirectory, "Scripts", script) }.Concat(arguments))
            info.ArgumentList.Add(argument);
        var error = new StringBuilder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await ProcessTransport.RunAsync(info, _ => { }, chunk => error.Append(chunk), cancellation.Token);
        return (exit, error.ToString());
    }
}
