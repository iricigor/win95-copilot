#requires -Version 7.0
param(
    [Parameter(Mandatory)] [string] $Scenario,
    [string] $ActualPackageDirectory,
    [string] $ActualRevision,
    [string] $ActualVersion,
    [string] $ActualRunNumber
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scripts = Join-Path (Split-Path $PSScriptRoot) 'Scripts'
Import-Module (Join-Path $scripts 'Prerelease.psm1') -Force
$directory = Join-Path ([IO.Path]::GetTempPath()) ("winchat-prerelease-fixture-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
function Assert($Condition, $Message) { if (-not $Condition) { throw "Assertion failed: $Message" } }
function MustFail([scriptblock] $Action, [string] $Message) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert ($null -ne $failure) "Expected failure: $Message"
    Assert ($failure.ToString().Contains($Message)) "Expected '$Message', got '$failure'"
}
function Git([string[]] $Arguments) {
    $output = & git.exe -C $directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Git fixture failed.' }
    return $output
}
function Commit {
    Git @('add', '--all') | Out-Null
    Git @('-c', 'user.name=Release Fixture', '-c', 'user.email=fixture@example.invalid',
        '-c', 'commit.gpgSign=false', 'commit', '--quiet', '--allow-empty', '-m', 'Synthetic source') | Out-Null
    return (Git @('rev-parse', 'HEAD'))
}
function WriteFixture([string] $Name, [string] $Content = 'synthetic') {
    $path = Join-Path $directory $Name
    [IO.Directory]::CreateDirectory((Split-Path $path)) | Out-Null
    [IO.File]::WriteAllText($path, $Content)
}
function Packages([string] $Root, [string] $Revision, [string] $Run = '123') {
    [IO.Directory]::CreateDirectory($Root) | Out-Null
    foreach ($contained in @($false, $true)) {
        $variant = if ($contained) { 'self-contained' } else { 'framework-dependent' }
        $name = "WinChatCopilot-1.2.3-preview.$Run-win-x64-$variant"
        $folder = Join-Path $Root $name
        [IO.Directory]::CreateDirectory($folder) | Out-Null
        foreach ($file in @('WinChatCopilot.exe', 'WinChatCopilot.dll', 'WinChatCopilot.deps.json', 'USER-GUIDE.md')) {
            [IO.File]::WriteAllText((Join-Path $folder $file), 'synthetic package fixture, not real packaging evidence')
        }
        $options = @{ tfm = 'net10.0' }
        $frameworks = @(@{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' })
        if ($contained) {
            $options.includedFrameworks = $frameworks
            foreach ($file in @('coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll')) {
                [IO.File]::WriteAllText((Join-Path $folder $file), 'synthetic runtime')
            }
        } else { $options.frameworks = $frameworks }
        @{ runtimeOptions = $options } | ConvertTo-Json -Depth 5 |
            Set-Content (Join-Path $folder 'WinChatCopilot.runtimeconfig.json') -Encoding utf8NoBOM
        $zip = Join-Path $Root "$name.zip"
        Compress-Archive -LiteralPath $folder -DestinationPath $zip
        @{
            ApplicationVersion = '1.2.3'; ReleaseVersion = "1.2.3-preview.$Run"; SourceRevision = $Revision
            RuntimeIdentifier = 'win-x64'; SelfContained = $contained; SdkVersion = '10.0.100'
            PublishedUtc = [DateTime]::UtcNow.ToString('o'); Archive = "$name.zip"
            ArchiveSha256 = (Get-FileHash $zip -Algorithm SHA256).Hash
            Files = @(Get-ChildItem $folder -File | ForEach-Object {
                @{ Name = $_.Name; Bytes = $_.Length; Sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
            })
        } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Root "$name.sha256.json") -Encoding utf8NoBOM
    }
}
try {
    if ($Scenario -eq 'workflow') {
        $workflow = Get-Content (Join-Path $PSScriptRoot 'windows-tests.yml') -Raw
        Assert ($workflow.Contains("permissions:`n  contents: read") -or $workflow.Contains("permissions:`r`n  contents: read")) 'read-only default'
        Assert ($workflow.Contains("branches: [main]") -and $workflow.Contains('pull_request:') -and $workflow.Contains('workflow_dispatch:')) 'routine triggers'
        Assert (-not $workflow.Contains('paths-ignore:')) 'routine documentation checks retained'
        Assert ($workflow.Contains("needs: [build-and-test, package]")) 'same-run successful prerequisites'
        Assert ($workflow.Contains("needs.build-and-test.result == 'success'") -and $workflow.Contains("needs.package.result == 'success'")) 'failed checks gate'
        Assert ($workflow.Contains("github.event_name == 'push' && github.ref == 'refs/heads/main'")) 'main push only'
        Assert ($workflow.Contains('ref: ${{ github.sha }}') -and $workflow.Contains('fetch-depth: 0')) 'exact SHA complete comparison'
        Assert ($workflow.Contains('group: prerelease-${{ github.run_id }}') -and $workflow.Contains('cancel-in-progress: false')) 'distinct push runs never replace each other'
        Assert (-not $workflow.Contains('github.run_attempt')) 'stable rerun identifier'
        Assert ($workflow.Contains('contents: write') -and $workflow.IndexOf('contents: write') -gt $workflow.IndexOf('  publish:')) 'write permissions only publication'
        Assert ($workflow.Contains('prerelease-packages-$env:GITHUB_RUN_ID') -and $workflow.Contains('gh run download')) 'original assets restored on retry'
        $publish = Get-Content (Join-Path $scripts 'Prerelease.psm1') -Raw
        Assert ($publish.Contains("make_latest = 'false'") -and $publish.Contains('prerelease = $true') -and $publish.Contains('draft = $true')) 'draft/prerelease/not latest'
        Assert ($publish.Contains('sha = $Revision') -and $publish.Contains('target_commitish = $Revision')) 'revision pinned tag/release'
    } elseif ($Scenario -in @('paths', 'comparison-errors', 'excluded-events', 'failed-validation')) {
        Git @('init', '--quiet') | Out-Null
        WriteFixture 'README.md'
        $before = Commit
        $event = @{ ref = 'refs/heads/main'; deleted = $false; before = $before; after = $before }
        if ($Scenario -eq 'paths') {
            $cases = @(
                @{ Paths = @('README.md', 'nested\guide.md', '.github\notes.md', 'docs\images\a.png'); Eligible = $false },
                @{ Paths = @('src\code.cs'); Eligible = $true },
                @{ Paths = @('docs\guide.md', 'source.cs'); Eligible = $true },
                @{ Paths = @('.github\workflows\pipeline.yml'); Eligible = $true }
            )
            foreach ($case in $cases) {
                $event.before = Commit
                foreach ($path in $case.Paths) { WriteFixture $path ([Guid]::NewGuid().ToString()) }
                $event.after = Commit
                $result = Get-PrereleaseDecision push $event $event.after $directory
                Assert ($result.Eligible -eq $case.Eligible) "path classification: $($case.Paths)"
            }
            $event.before = Commit
            $event.after = Commit
            Assert (-not (Get-PrereleaseDecision push $event $event.after $directory).Eligible) 'empty diff'
            WriteFixture 'deleted.cs'; $event.before = Commit
            Remove-Item (Join-Path $directory 'deleted.cs'); $event.after = Commit
            Assert ((Get-PrereleaseDecision push $event $event.after $directory).Eligible) 'code deletion'
            foreach ($pair in @(@('source.cs', 'docs\renamed.md'), @('docs\renamed.md', 'renamed.cs'))) {
                $event.before = Commit
                Move-Item (Join-Path $directory $pair[0]) (Join-Path $directory $pair[1]); $event.after = Commit
                $result = Get-PrereleaseDecision push $event $event.after $directory
                Assert ($result.Eligible -and $result.Paths.Count -eq 2) 'both rename paths'
            }
            $event.before = Commit
            for ($index = 0; $index -lt 350; $index++) { WriteFixture "docs\many\$index.md" }
            Commit | Out-Null
            WriteFixture 'many\last.cs'; $event.after = Commit
            $result = Get-PrereleaseDecision push $event $event.after $directory
            Assert ($result.Eligible -and $result.Paths.Count -eq 351) 'complete multi-commit diff beyond API 300-path limit'
            $event.before = '0' * 40
            Assert ((Get-PrereleaseDecision push $event $event.after $directory).Eligible) 'new main branch full tree'
        } elseif ($Scenario -eq 'comparison-errors') {
            foreach ($bad in @('', 'missing', ('f' * 40))) {
                $event.before = $bad
                MustFail { Get-PrereleaseDecision push $event $before $directory } $(if ($bad.Length -eq 40) { 'Git command failed' } else { 'Invalid' })
            }
            $event.before = $before; $event.after = 'f' * 40
            MustFail { Get-PrereleaseDecision push $event $before $directory } 'Invalid'
            WriteFixture 'new.cs'; $event.after = Commit
            MustFail { Get-PrereleaseDecision push $event $before $directory } 'Invalid'
        } else {
            [IO.Directory]::CreateDirectory((Join-Path $directory 'scripts')) | Out-Null
            Get-ChildItem $scripts -File | Copy-Item -Destination (Join-Path $directory 'scripts')
            WriteFixture 'source.cs'; $event.after = Commit
            $env:GITHUB_SHA = $event.after
            $env:GITHUB_EVENT_PATH = Join-Path $directory '.git\event.json'
            $env:GITHUB_REPOSITORY = 'iricigor/win95-copilot'
            $env:GH_TOKEN = 'synthetic-never-used'
            $entry = Join-Path $directory 'scripts\Publish-GitHubPrerelease.ps1'
            if ($Scenario -eq 'failed-validation') {
                $env:GITHUB_EVENT_NAME = 'push'
                $event | ConvertTo-Json | Set-Content $env:GITHUB_EVENT_PATH
                foreach ($state in @('failure', 'cancelled', 'skipped', '')) {
                    MustFail { & $entry -PackageDirectory 'missing' -ValidatedRevision $event.after -ValidationResult $state -PackagingResult success } $(if ($state) { 'successful same-SHA' } else { 'empty string' })
                }
                MustFail { & $entry -PackageDirectory 'missing' -ValidatedRevision $before -ValidationResult success -PackagingResult success } 'successful same-SHA'
                MustFail { & $entry -PackageDirectory 'missing' -ValidatedRevision $event.after -ValidationResult success -PackagingResult failure } 'successful same-SHA'
            } else {
                foreach ($name in @('pull_request', 'workflow_dispatch', 'push')) {
                    $env:GITHUB_EVENT_NAME = $name
                    $event.ref = if ($name -eq 'push') { 'refs/heads/feature' } else { 'refs/heads/main' }
                    $event | ConvertTo-Json | Set-Content $env:GITHUB_EVENT_PATH
                    & $entry -PackageDirectory 'missing' -ValidatedRevision invalid -ValidationResult failure -PackagingResult failure
                }
                $event.ref = 'refs/tags/v1'; $event | ConvertTo-Json | Set-Content $env:GITHUB_EVENT_PATH
                & $entry -PackageDirectory 'missing' -ValidatedRevision invalid -ValidationResult failure -PackagingResult failure
                $event.ref = 'refs/heads/main'; $event.deleted = $true
                $event | ConvertTo-Json | Set-Content $env:GITHUB_EVENT_PATH
                & $entry -PackageDirectory 'missing' -ValidatedRevision invalid -ValidationResult failure -PackagingResult failure
                $event.deleted = $false; $event.before = $event.after
                $event | ConvertTo-Json | Set-Content $env:GITHUB_EVENT_PATH
                & $entry -PackageDirectory 'missing' -ValidatedRevision invalid -ValidationResult failure -PackagingResult failure
                $event.before = $event.after; WriteFixture 'docs\only.md'; $event.after = Commit
                $env:GITHUB_SHA = $event.after
                $event | ConvertTo-Json | Set-Content $env:GITHUB_EVENT_PATH
                & $entry -PackageDirectory 'missing' -ValidatedRevision invalid -ValidationResult failure -PackagingResult failure
            }
        }
    } else {
        $revision = if ($ActualPackageDirectory) { $ActualRevision } else { 'a' * 40 }
        $version = if ($ActualPackageDirectory) { $ActualVersion } else { '1.2.3' }
        $run = if ($ActualPackageDirectory) { $ActualRunNumber } else { '123' }
        $packages = if ($ActualPackageDirectory) { $ActualPackageDirectory } else { Join-Path $directory 'packages' }
        if (-not $ActualPackageDirectory) { Packages $packages $revision }
        $state = @{
            Tags = @{}; Releases = [Collections.Generic.List[object]]::new()
            Assets = @{}; Files = @{}; Writes = [Collections.Generic.List[string]]::new()
            Next = 1; FailAfter = -1; Deny = $false; Corrupt = $false
        }
        $api = {
            param($Method, $Path, $Body, $UploadFile, $DownloadFile)
            if ($state.Deny) { throw 'Synthetic HTTP 403 permission failure' }
            if ($Method -ne 'GET') { $state.Writes.Add("$Method $Path") }
            if ($DownloadFile) {
                $id = [int] ($Path.Split('/')[-1])
                Copy-Item -LiteralPath $state.Files[$id] -Destination $DownloadFile
                if ($state.Corrupt) { Add-Content -LiteralPath $DownloadFile -Value 'tampered' }
                return
            }
            if ($Path -match '/git/ref/tags/(.+)$') { return $state.Tags[$Matches[1]] }
            if ($Path.EndsWith('/git/refs')) {
                $state.Tags[$Body.ref.Substring(10)] = @{ object = @{ type = 'commit'; sha = $Body.sha } }
                return $state.Tags[$Body.ref.Substring(10)]
            }
            if ($Path -match '/releases\?per_page=100&page=(\d+)$') {
                if ($Matches[1] -eq '1') { return $state.Releases.ToArray() }
                return @()
            }
            if ($Path.EndsWith('/releases') -and $Method -eq 'POST') {
                $release = [pscustomobject]@{
                    id = $state.Next++; tag_name = $Body.tag_name; target_commitish = $Body.target_commitish
                    body = $Body.body; draft = $Body.draft; prerelease = $Body.prerelease
                    html_url = 'https://example.invalid/synthetic-release'
                }
                Assert ($Body.draft -and $Body.prerelease -and $Body.make_latest -eq 'false') 'created unpublished prerelease'
                $state.Releases.Add($release); $state.Assets[$release.id] = [Collections.Generic.List[object]]::new()
                return $release
            }
            if ($Path -match '/releases/(\d+)/assets\?name=(.+)$') {
                $releaseId = [int] $Matches[1]; $name = [Uri]::UnescapeDataString($Matches[2])
                if ($state.FailAfter -eq $state.Assets[$releaseId].Count) { throw 'Synthetic upload failure' }
                $id = $state.Next++
                $storage = Join-Path $directory "asset-$id"
                Copy-Item -LiteralPath $UploadFile -Destination $storage
                $state.Files[$id] = $storage
                $asset = [pscustomobject]@{ id = $id; name = $name; size = (Get-Item $storage).Length; state = 'uploaded' }
                $state.Assets[$releaseId].Add($asset)
                return $asset
            }
            if ($Path -match '/releases/(\d+)/assets\?per_page=100&page=(\d+)$') {
                if ($Matches[2] -eq '1') { return $state.Assets[[int] $Matches[1]].ToArray() }
                return @()
            }
            if ($Path -match '/releases/(\d+)$') {
                $release = $state.Releases | Where-Object id -eq ([int] $Matches[1])
                if ($Method -eq 'PATCH') {
                    Assert ($state.Assets[$release.id].Count -eq 4) 'all assets before publish'
                    Assert ($Body.prerelease -and $Body.make_latest -eq 'false') 'prerelease/not latest'
                    $release.draft = $Body.draft
                }
                return $release
            }
            throw "Unexpected synthetic API call $Method $Path"
        }.GetNewClosure()
        $invoke = {
            Publish-VerifiedPrerelease -Repository iricigor/win95-copilot -Revision $revision -Version $version `
                -RunNumber $run -PackageDirectory $packages -Api $api
        }
        switch ($Scenario) {
            'conflicting-tag' {
                $state.Tags['v1.2.3-preview.123'] = @{ object = @{ type = 'commit'; sha = 'b' * 40 } }
                MustFail $invoke 'Conflicting prerelease tag'
                Assert ($state.Writes.Count -eq 0) 'no writes on tag conflict'
            }
            'bad-package' {
                Add-Content (Get-ChildItem $packages -Filter '*.zip' | Select-Object -First 1).FullName 'tampered'
                MustFail $invoke 'Archive SHA-256'
                Assert ($state.Writes.Count -eq 0) 'no writes on failed packaging'
            }
            { $_ -in @('api-failure', 'permission-failure') } {
                $state.Deny = $true
                MustFail $invoke 'HTTP 403'
                Assert ($state.Writes.Count -eq 0) 'no writes on API/permission error'
            }
            'partial-retry' {
                $state.FailAfter = 2
                MustFail $invoke 'Synthetic upload failure'
                Assert ($state.Releases[0].draft -and $state.Assets[$state.Releases[0].id].Count -eq 2) 'partial draft retained'
                $state.FailAfter = -1; & $invoke | Out-Null
                Assert ($state.Releases.Count -eq 1 -and $state.Tags.Count -eq 1) 'retry no duplicates'
            }
            'corrupt-download' {
                $state.Corrupt = $true; MustFail $invoke 'SHA-256 mismatch'
                Assert ($state.Releases[0].draft) 'bad downloaded bytes never publish'
            }
            default {
                & $invoke | Out-Null
                Assert (-not $state.Releases[0].draft -and $state.Releases[0].prerelease) 'first published'
                $writes = $state.Writes.Count
                if ($Scenario -in @('repeat', 'published', 'actual-packages')) {
                    & $invoke | Out-Null
                    Assert ($state.Writes.Count -eq $writes -and $state.Releases.Count -eq 1) 'published repeat read-only verification'
                    if ($Scenario -eq 'actual-packages') {
                        Assert ($ActualPackageDirectory -and $state.Assets[$state.Releases[0].id].Count -eq 4) 'actual package assets consumed and downloaded'
                    }
                } elseif ($Scenario -eq 'conflicting-revision') {
                    $state.Releases[0].target_commitish = 'b' * 40
                    MustFail $invoke 'Conflicting release revision'
                    Assert ($state.Writes.Count -eq $writes) 'conflicting ledger read only'
                } elseif ($Scenario -eq 'incomplete-published') {
                    $state.Assets[$state.Releases[0].id].RemoveAt(0)
                    MustFail $invoke 'Published release is incomplete'
                    Assert ($state.Writes.Count -eq $writes) 'never repair published incomplete assets'
                } elseif ($Scenario -eq 'unexpected-asset') {
                    $state.Assets[$state.Releases[0].id].Add([pscustomobject]@{ name = 'unexpected.zip' })
                    MustFail $invoke 'Unexpected or duplicate'
                    Assert ($state.Writes.Count -eq $writes) 'unexpected assets never deleted'
                } elseif ($Scenario -eq 'different-runs') {
                    $second = Join-Path $directory 'second'
                    Packages $second ('b' * 40) 124
                    Publish-VerifiedPrerelease iricigor/win95-copilot ('b' * 40) 1.2.3 124 $second $api | Out-Null
                    Assert ($state.Releases.Count -eq 2 -and $state.Tags.Count -eq 2) 'distinct push runs retain separate releases'
                } else { Assert ($Scenario -eq 'first') 'known scenario' }
            }
        }
    }
    "PASS $Scenario"
} finally {
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse -Force) { $file.Attributes = [IO.FileAttributes]::Normal }
    Remove-Item -LiteralPath $directory -Recurse -Force
}
