#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-ReleaseGit {
    param([string] $RepositoryDirectory, [string[]] $Arguments)
    $info = [Diagnostics.ProcessStartInfo]::new('git')
    $info.WorkingDirectory = $RepositoryDirectory
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Push comparison Git command failed: $($errorOutput.Result)" }
        return $output.Result
    } finally { $process.Dispose() }
}

function Get-PrereleaseDecision {
    param(
        [Parameter(Mandatory)] [string] $EventName,
        [Parameter(Mandatory)] [object] $Event,
        [Parameter(Mandatory)] [string] $Revision,
        [Parameter(Mandatory)] [string] $RepositoryDirectory
    )
    if ($EventName -cne 'push' -or $Event.ref -cne 'refs/heads/main' -or $Event.deleted) {
        return [pscustomobject]@{ Eligible = $false; Reason = 'Not a live main push'; Paths = @() }
    }
    if ($Revision -cnotmatch '^[0-9a-f]{40}$' -or $Event.after -cne $Revision -or
        $Event.before -cnotmatch '^[0-9a-f]{40}$' -or $Revision -eq ('0' * 40)) {
        throw 'Invalid or mismatched full before/after push revision.'
    }
    $head = (Invoke-ReleaseGit $RepositoryDirectory @('rev-parse', '--verify', 'HEAD')).Trim()
    if ($head -cne $Revision) { throw 'Push revision does not match the checked-out HEAD.' }
    Invoke-ReleaseGit $RepositoryDirectory @('cat-file', '-e', "$Revision^{commit}") | Out-Null
    if ($Event.before -eq ('0' * 40)) {
        $names = Invoke-ReleaseGit $RepositoryDirectory @('ls-tree', '-r', '--name-only', '-z', $Revision)
    } else {
        Invoke-ReleaseGit $RepositoryDirectory @('cat-file', '-e', "$($Event.before)^{commit}") | Out-Null
        # Disabling rename detection includes both old and new paths, including deletions.
        $names = Invoke-ReleaseGit $RepositoryDirectory @('diff', '--name-only', '--no-renames', '-z', $Event.before, $Revision, '--')
    }
    if ($names.Length -gt 0 -and -not $names.EndsWith("`0")) { throw 'Incomplete NUL-delimited push comparison.' }
    $paths = @($names.Split("`0", [StringSplitOptions]::RemoveEmptyEntries))
    $code = @($paths | Where-Object {
        -not $_.StartsWith('docs/', [StringComparison]::Ordinal) -and
        -not $_.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)
    })
    [pscustomobject]@{
        Eligible = $code.Count -gt 0
        Reason = if ($code.Count) { 'Non-documentation paths changed' } elseif ($paths.Count) { 'Documentation only' } else { 'Empty diff' }
        Paths = $paths
    }
}

function Get-PrereleasePackages {
    param([string] $Directory, [string] $Version, [string] $Revision, [string] $RunNumber)
    if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $Revision -cnotmatch '^[0-9a-f]{40}$' -or $RunNumber -cnotmatch '^[1-9][0-9]*$') {
        throw 'Invalid prerelease version, revision or run number.'
    }
    $releaseVersion = "$Version-preview.$RunNumber"
    $assets = @()
    foreach ($variant in @('framework-dependent', 'self-contained')) {
        $name = "WinChatCopilot-$releaseVersion-win-x64-$variant"
        $manifestPath = Join-Path $Directory "$name.sha256.json"
        & (Join-Path $PSScriptRoot 'Test-Package.ps1') -ManifestPath $manifestPath | Out-Null
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.SourceRevision -cne $Revision -or $manifest.ApplicationVersion -cne $Version -or
            $manifest.ReleaseVersion -cne $releaseVersion) { throw 'Package provenance does not match this validated run.' }
        foreach ($extension in @('.zip', '.sha256.json')) {
            $path = Join-Path $Directory "$name$extension"
            $assets += [pscustomobject]@{
                Name = [IO.Path]::GetFileName($path)
                Path = $path
                Size = (Get-Item -LiteralPath $path).Length
                Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
    return $assets
}

function Publish-VerifiedPrerelease {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string] $Revision,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $RunNumber,
        [Parameter(Mandatory)] [string] $PackageDirectory,
        [Parameter(Mandatory)] [scriptblock] $Api
    )
    if ($Repository -cne 'iricigor/win95-copilot') { throw 'Unexpected publication repository.' }
    $assets = @(Get-PrereleasePackages $PackageDirectory $Version $Revision $RunNumber)
    $identifier = "$Version-preview.$RunNumber"
    $tag = "v$identifier"
    $root = "/repos/$Repository"
    $ledger = [ordered]@{
        Schema = 'winchat-prerelease-v1'; SourceRevision = $Revision
        ApplicationVersion = $Version; ReleaseVersion = $identifier
        Assets = @($assets | Select-Object Name, Size, Sha256)
    } | ConvertTo-Json -Depth 5 -Compress
    $body = @"
Automated Windows x64 prerelease **$identifier**, base application version **$Version**.
Source revision: https://github.com/$Repository/commit/$Revision

Download/extract the framework-dependent ZIP with .NET 10 Desktop Runtime x64 installed, or use the self-contained ZIP with the runtime included. Keep all extracted files together. Copilot is not bundled.

Each ZIP has its own SHA-256 JSON manifest. Compare the ZIP's SHA-256 with ArchiveSha256 (PowerShell: Get-FileHash -Algorithm SHA256), then use scripts\Test-Package.ps1 from this exact revision to verify every packaged file and runtime declaration.

<!-- winchat-prerelease-ledger $ledger -->
"@
    $reference = & $Api 'GET' "$root/git/ref/tags/$tag" $null $null $null
    if ($null -ne $reference) {
        $target = $reference.object
        for ($depth = 0; $target.type -ceq 'tag' -and $depth -lt 8; $depth++) {
            $annotated = & $Api 'GET' "$root/git/tags/$($target.sha)" $null $null $null
            $target = $annotated.object
        }
        if ($target.type -cne 'commit' -or $target.sha -cne $Revision) { throw 'Conflicting prerelease tag target.' }
    }
    $matching = @()
    for ($page = 1; ; $page++) {
        $releases = @(& $Api 'GET' "$root/releases?per_page=100&page=$page" $null $null $null)
        $matching += @($releases | Where-Object { $_.tag_name -ceq $tag })
        if ($releases.Count -lt 100) { break }
    }
    if ($matching.Count -gt 1) { throw 'Duplicate releases for this tag.' }
    $release = if ($matching.Count) { $matching[0] } else { $null }
    if ($null -ne $release -and ($release.body -cne $body -or -not $release.prerelease -or
        $release.target_commitish -cne $Revision)) { throw 'Conflicting release revision or asset ledger.' }
    if ($null -eq $reference -and $null -ne $release -and -not $release.draft) {
        throw 'Published release is missing its revision-pinned tag.'
    }
    if ($null -eq $reference) {
        & $Api 'POST' "$root/git/refs" @{ ref = "refs/tags/$tag"; sha = $Revision } $null $null | Out-Null
    }
    if ($null -eq $release) {
        $release = & $Api 'POST' "$root/releases" @{
            tag_name = $tag; target_commitish = $Revision; name = $identifier
            body = $body; draft = $true; prerelease = $true; make_latest = 'false'
        } $null $null
    }
    $remote = @()
    for ($page = 1; ; $page++) {
        $batch = @(& $Api 'GET' "$root/releases/$($release.id)/assets?per_page=100&page=$page" $null $null $null)
        $remote += $batch
        if ($batch.Count -lt 100) { break }
    }
    $expected = @($assets.Name)
    if (@($remote | Where-Object { $_.name -cnotin $expected }).Count -or
        @($remote | ForEach-Object { $_.name } | Group-Object | Where-Object Count -gt 1).Count) {
        throw 'Unexpected or duplicate release assets; nothing is overwritten.'
    }
    $downloadDirectory = Join-Path ([IO.Path]::GetTempPath()) ("winchat-release-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($downloadDirectory) | Out-Null
    try {
        foreach ($asset in $assets) {
            $found = @($remote | Where-Object { $_.name -ceq $asset.Name })
            if (-not $found.Count) {
                if (-not $release.draft) { throw 'Published release is incomplete; assets are never replaced.' }
                & $Api 'POST' "$root/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($asset.Name))" $null $asset.Path $null | Out-Null
            }
        }
        $remote = @(& $Api 'GET' "$root/releases/$($release.id)/assets?per_page=100&page=1" $null $null $null)
        if ($remote.Count -ne 4) { throw 'Release must contain exactly four assets before publication.' }
        foreach ($asset in $assets) {
            $found = @($remote | Where-Object { $_.name -ceq $asset.Name })
            if ($found.Count -ne 1 -or $found[0].size -ne $asset.Size -or $found[0].state -cne 'uploaded') {
                throw "Incomplete or conflicting release asset: $($asset.Name)"
            }
            $download = Join-Path $downloadDirectory $asset.Name
            & $Api 'GET' "$root/releases/assets/$($found[0].id)" $null $null $download | Out-Null
            if ((Get-Item -LiteralPath $download).Length -ne $asset.Size -or
                (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $asset.Sha256) {
                throw "Downloaded release asset SHA-256 mismatch: $($asset.Name)"
            }
        }
        Get-PrereleasePackages $downloadDirectory $Version $Revision $RunNumber | Out-Null
        $reference = & $Api 'GET' "$root/git/ref/tags/$tag" $null $null $null
        if ($reference.object.type -cne 'commit' -or $reference.object.sha -cne $Revision) {
            throw 'Tag changed or is not the exact run-owned commit reference before publication.'
        }
        if ($release.draft) {
            & $Api 'PATCH' "$root/releases/$($release.id)" @{
                draft = $false; prerelease = $true; make_latest = 'false'
            } $null $null | Out-Null
        }
        $final = & $Api 'GET' "$root/releases/$($release.id)" $null $null $null
        if ($final.draft -or -not $final.prerelease -or $final.body -cne $body -or
            $final.target_commitish -cne $Revision -or $final.tag_name -cne $tag) {
            throw 'Final prerelease publication could not be verified.'
        }
        return $final.html_url
    } finally {
        foreach ($file in Get-ChildItem -LiteralPath $downloadDirectory -File) { Remove-Item -LiteralPath $file.FullName }
        [IO.Directory]::Delete($downloadDirectory)
    }
}

Export-ModuleMember -Function Get-PrereleaseDecision, Get-PrereleasePackages, Publish-VerifiedPrerelease
