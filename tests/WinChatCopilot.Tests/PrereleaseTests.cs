using System.Diagnostics;
using System.IO;
using System.Text;
using WinChatCopilot.Processes;

namespace WinChatCopilot.Tests;

public class PrereleaseTests
{
    [Theory]
    [InlineData("paths")]
    [InlineData("comparison-errors")]
    [InlineData("excluded-events")]
    [InlineData("workflow")]
    [InlineData("first")]
    [InlineData("partial-retry")]
    [InlineData("repeat")]
    [InlineData("published")]
    [InlineData("conflicting-tag")]
    [InlineData("conflicting-revision")]
    [InlineData("incomplete-published")]
    [InlineData("unexpected-asset")]
    [InlineData("corrupt-download")]
    [InlineData("api-failure")]
    [InlineData("permission-failure")]
    [InlineData("bad-package")]
    [InlineData("different-runs")]
    [InlineData("failed-validation")]
    public async Task PrereleaseScriptsEnforcePublicationContract(string scenario)
    {
        var powershell = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe")
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Prerelease tests require PowerShell 7.");
        var gitDirectory = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => path.Trim('"'))
            .Concat([
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd")
            ]).FirstOrDefault(path => File.Exists(Path.Combine(path, "git.exe")))
            ?? throw new FileNotFoundException("Prerelease comparison tests require Git.");
        var info = new ProcessStartInfo(powershell) { WorkingDirectory = AppContext.BaseDirectory };
        info.Environment["PATH"] = gitDirectory + Path.PathSeparator + info.Environment["PATH"];
        // The synthetic transport must never inherit a credential or real Actions event.
        foreach (var key in info.Environment.Keys.Where(key =>
                     key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase) ||
                     key.EndsWith("TOKEN", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(AppContext.BaseDirectory, "Fixtures", "PrereleaseFixture.ps1"),
                     "-Scenario", scenario
                 })
            info.ArgumentList.Add(argument);
        var error = new StringBuilder();
        var output = new StringBuilder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var exit = await ProcessTransport.RunAsync(info, chunk => output.Append(chunk),
            chunk => error.Append(chunk), cancellation.Token);
        Assert.True(exit == 0, $"{scenario}: {error}\n{output}");
        Assert.Contains("PASS " + scenario, output.ToString());
    }
}
