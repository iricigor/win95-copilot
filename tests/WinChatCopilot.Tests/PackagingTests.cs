using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-preview.0")]
    [InlineData("1.2.3-preview.01")]
    [InlineData("1.2.3-preview.1.2")]
    [InlineData("1.2.3-preview.1+build")]
    [InlineData("2.0.0-preview.1")]
    [InlineData("../unsafe")]
    public async Task InvalidReleaseVersionsFailBeforeDotnet(string version)
    {
        using var fixture = await PackageFixture.Create();
        var result = await fixture.Publish("-ReleaseVersion", version, "-SourceRevision", fixture.Head);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("ReleaseVersion", result.Error);
        Assert.False(File.Exists(fixture.Invocations));
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("0000000000000000000000000000000000000000")]
    public async Task InvalidOrDifferentRevisionFailsBeforeDotnet(string revision)
    {
        using var fixture = await PackageFixture.Create();
        var result = await fixture.Publish("-ReleaseVersion", "1.2.3-preview.123", "-SourceRevision", revision);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("SourceRevision", result.Error);
        Assert.False(File.Exists(fixture.Invocations));
    }

    [Fact]
    public async Task PrereleaseRequiresExplicitRevisionAndCleanCheckout()
    {
        using var fixture = await PackageFixture.Create();
        var absent = await fixture.Publish("-ReleaseVersion", "1.2.3-preview.123");
        Assert.NotEqual(0, absent.Exit);
        Assert.Contains("explicit SourceRevision", absent.Error);
        await File.AppendAllTextAsync(Path.Combine(fixture.Repo, "docs", "user-guide.md"), "\ndirty");
        var dirty = await fixture.Publish("-ReleaseVersion", "1.2.3-preview.123", "-SourceRevision", fixture.Head);
        Assert.NotEqual(0, dirty.Exit);
        Assert.Contains("clean working tree", dirty.Error);
        Assert.False(File.Exists(fixture.Invocations));
    }

    [Theory]
    [InlineData("failure", "Distribution publish failed")]
    [InlineData("missing", "application executable")]
    [InlineData("sdk-failure", "SDK is unavailable")]
    public async Task PublishFailuresNeverProduceSuccessAssets(string mode, string expected)
    {
        using var fixture = await PackageFixture.Create(mode);
        var result = await fixture.Publish();
        Assert.NotEqual(0, result.Exit);
        Assert.Contains(expected, result.Error);
        Assert.Empty(Directory.Exists(fixture.Output)
            ? Directory.EnumerateFiles(fixture.Output, "*.sha256.json") : []);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LocalAndPrereleaseInvocationsPreserveBaseVersionAndVerifyEveryFile(bool selfContained, bool prerelease)
    {
        using var fixture = await PackageFixture.Create();
        var arguments = new List<string>();
        if (selfContained) arguments.Add("-SelfContained");
        if (prerelease) arguments.AddRange(["-ReleaseVersion", "1.2.3-preview.123", "-SourceRevision", fixture.Head.ToUpperInvariant()]);
        var result = await fixture.Publish(arguments.ToArray());
        Assert.True(result.Exit == 0, result.Error);
        var manifestPath = Assert.Single(Directory.GetFiles(fixture.Output, "*.sha256.json"));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        Assert.Equal("1.2.3", manifest["ApplicationVersion"]!.GetValue<string>());
        Assert.Equal(prerelease ? "1.2.3-preview.123" : "1.2.3", manifest["ReleaseVersion"]!.GetValue<string>());
        Assert.Equal(fixture.Head, manifest["SourceRevision"]!.GetValue<string>());
        Assert.Equal(selfContained, manifest["SelfContained"]!.GetValue<bool>());
        Assert.Equal("win-x64", manifest["RuntimeIdentifier"]!.GetValue<string>());
        Assert.Contains(selfContained ? "self-contained" : "framework-dependent", Path.GetFileName(manifestPath));
        Assert.Contains(manifest["Files"]!.AsArray(), file => file!["Name"]!.GetValue<string>() == "nested/fixture.txt");
        var verified = await RunScript("Test-Package.ps1", ["-ManifestPath", manifestPath]);
        Assert.True(verified.Exit == 0, verified.Error);
        var invocation = await File.ReadAllTextAsync(fixture.Invocations);
        Assert.Contains("--configuration Release --runtime win-x64", invocation);
        Assert.Contains("--self-contained " + selfContained, invocation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-p:PublishSingleFile=false -p:PublishTrimmed=false", invocation);
        Assert.DoesNotContain("-p:Version", invocation);
    }

    [Theory]
    [InlineData("archive", "Archive SHA-256")]
    [InlineData("file", "file size/SHA-256")]
    [InlineData("missing", "omits manifest-listed")]
    [InlineData("extra", "Unexpected or duplicate")]
    [InlineData("manifest", "Missing required packaged file")]
    [InlineData("variant", "Runtime configuration")]
    public async Task VerificationRejectsTamperingAndIncompleteOrMisdeclaredPackages(string mutation, string error)
    {
        using var fixture = await PackageFixture.Create();
        var published = await fixture.Publish();
        Assert.True(published.Exit == 0, published.Error);
        var manifestPath = Assert.Single(Directory.GetFiles(fixture.Output, "*.sha256.json"));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        var archive = Path.Combine(fixture.Output, manifest["Archive"]!.GetValue<string>());
        var root = Path.GetFileNameWithoutExtension(archive);
        if (mutation == "archive") await File.AppendAllTextAsync(archive, "tampered");
        else if (mutation == "manifest")
        {
            var files = manifest["Files"]!.AsArray();
            files.Remove(files.Single(file => file!["Name"]!.GetValue<string>() == "WinChatCopilot.exe"));
        }
        else if (mutation == "variant")
        {
            manifest["SelfContained"] = true;
            var renamed = root.Replace("framework-dependent", "self-contained", StringComparison.Ordinal);
            var renamedArchive = Path.Combine(fixture.Output, renamed + ".zip");
            using (var source = ZipFile.OpenRead(archive))
            using (var target = ZipFile.Open(renamedArchive, ZipArchiveMode.Create))
                foreach (var entry in source.Entries)
                {
                    var copy = target.CreateEntry(entry.FullName.Replace(root, renamed, StringComparison.Ordinal));
                    using var input = entry.Open();
                    using var output = copy.Open();
                    input.CopyTo(output);
                }
            archive = renamedArchive;
            manifest["Archive"] = renamed + ".zip";
            manifestPath = Path.Combine(fixture.Output, renamed + ".sha256.json");
        }
        else
        {
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
            if (mutation == "extra")
            {
                using var writer = new StreamWriter(zip.CreateEntry(root + "/unlisted.txt").Open());
                writer.Write("unexpected");
            }
            else
            {
                zip.GetEntry(root + "/nested/fixture.txt")!.Delete();
                if (mutation == "file")
                {
                    using var writer = new StreamWriter(zip.CreateEntry(root + "/nested/fixture.txt").Open());
                    writer.Write("tampered file");
                }
            }
        }
        if (mutation != "archive")
            manifest["ArchiveSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive)));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var result = await RunScript("Test-Package.ps1", ["-ManifestPath", manifestPath]);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains(error, result.Error);
    }

    private sealed class PackageFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winchat-package-contract-" + Guid.NewGuid().ToString("N"));
        public string Repo => Path.Combine(directory, "repo");
        public string Output => Path.Combine(directory, "output");
        public string Invocations => Path.Combine(directory, "invocations.txt");
        public string Head { get; private set; } = "";

        public static async Task<PackageFixture> Create(string mode = "success")
        {
            var fixture = new PackageFixture();
            try
            {
                Directory.CreateDirectory(Path.Combine(fixture.Repo, "scripts"));
                Directory.CreateDirectory(Path.Combine(fixture.Repo, "docs"));
                foreach (var script in new[] { "Publish-App.ps1", "Test-Package.ps1" })
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Scripts", script), Path.Combine(fixture.Repo, "scripts", script));
                await File.WriteAllTextAsync(Path.Combine(fixture.Repo, "Directory.Build.props"),
                    "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>");
                await File.WriteAllTextAsync(Path.Combine(fixture.Repo, "docs", "user-guide.md"), "Synthetic guide");
                await File.WriteAllTextAsync(Path.Combine(fixture.directory, "dotnet.ps1"), """
                    $ErrorActionPreference = 'Stop'
                    $global:LASTEXITCODE = 0
                    Add-Content -LiteralPath (Join-Path $PSScriptRoot 'invocations.txt') -Value ($args -join ' ')
                    if ($args[0] -eq '--version') {
                        if ('MODE' -eq 'sdk-failure') { $global:LASTEXITCODE = 1; return }
                        '10.0.100'; return
                    }
                    if ('MODE' -eq 'failure') { $global:LASTEXITCODE = 2; return }
                    $destination = $args[[Array]::IndexOf($args, '--output') + 1]
                    [IO.Directory]::CreateDirectory($destination) | Out-Null
                    if ('MODE' -eq 'missing') { return }
                    foreach ($file in @('WinChatCopilot.exe', 'WinChatCopilot.dll', 'WinChatCopilot.deps.json')) {
                        Set-Content -LiteralPath (Join-Path $destination $file) -Value 'synthetic package file'
                    }
                    $contained = $args[[Array]::IndexOf($args, '--self-contained') + 1] -eq 'True'
                    $frameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.0' },
                                    @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' })
                    $options = @{ tfm = 'net10.0' }
                    if ($contained) {
                        $options.includedFrameworks = $frameworks
                        foreach ($file in @('coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll')) {
                            Set-Content -LiteralPath (Join-Path $destination $file) -Value 'synthetic runtime file'
                        }
                    } else { $options.frameworks = $frameworks }
                    @{ runtimeOptions = $options } | ConvertTo-Json -Depth 5 |
                        Set-Content -LiteralPath (Join-Path $destination 'WinChatCopilot.runtimeconfig.json')
                    [IO.Directory]::CreateDirectory((Join-Path $destination 'nested')) | Out-Null
                    Set-Content -LiteralPath (Join-Path $destination 'nested\fixture.txt') -Value 'nested synthetic file'
                    """.Replace("MODE", mode, StringComparison.Ordinal));
                foreach (var arguments in new[]
                         {
                             new[] { "init", "--quiet" }, new[] { "add", "." },
                             new[] { "-c", "user.name=Package Fixture", "-c", "user.email=fixture@example.invalid",
                                 "-c", "commit.gpgSign=false", "commit", "--quiet", "-m", "Synthetic package source" }
                         })
                {
                    var result = await RunProcess("git", fixture.Repo, arguments);
                    Assert.True(result.Exit == 0, result.Error);
                }
                var head = await RunProcess("git", fixture.Repo, ["rev-parse", "HEAD"]);
                Assert.True(head.Exit == 0, head.Error);
                fixture.Head = head.Output.Trim();
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public Task<(int Exit, string Error)> Publish(params string[] arguments) =>
            RunScript(Path.Combine(Repo, "scripts", "Publish-App.ps1"),
                new[] { "-OutputDirectory", Output, "-DotnetPath", Path.Combine(directory, "dotnet.ps1") }.Concat(arguments).ToArray());

        public void Dispose()
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    private static async Task<(int Exit, string Error)> RunScript(string script, string[] arguments)
    {
        var powershell = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe")
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            "Packaging script regressions require PowerShell 7 in its standard installation or app-execution-alias location.");
        var path = Path.IsPathFullyQualified(script) ? script : Path.Combine(AppContext.BaseDirectory, "Scripts", script);
        var result = await RunProcess(powershell, AppContext.BaseDirectory,
            new[] { "-NoProfile", "-NonInteractive", "-File", path }.Concat(arguments).ToArray());
        return (result.Exit, result.Error);
    }

    private static async Task<(int Exit, string Error, string Output)> RunProcess(string executable, string workingDirectory, string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        var error = new StringBuilder();
        var output = new StringBuilder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exit = await ProcessTransport.RunAsync(info, chunk => output.Append(chunk), chunk => error.Append(chunk), cancellation.Token);
        return (exit, error.ToString(), output.ToString());
    }
}
