#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $DotnetPath = 'dotnet',
    [switch] $SelfContained,
    [string] $ReleaseVersion,
    [string] $SourceRevision
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Publishing the WPF distribution requires Windows.' }
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    throw 'OutputDirectory must be an absolute, new directory.'
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'OutputDirectory already exists. Select a new directory; no existing distribution is overwritten.'
}
$repo = Split-Path $PSScriptRoot
$project = Join-Path $repo 'src\WinChatCopilot\WinChatCopilot.csproj'
[xml] $properties = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$version = [string] $properties.Project.PropertyGroup.Version
if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Directory.Build.props must specify a three-part base application version.'
}
if ($PSBoundParameters.ContainsKey('ReleaseVersion')) {
    if ($ReleaseVersion -cnotmatch "^$([regex]::Escape($version))-preview\.[1-9][0-9]*$") {
        throw 'ReleaseVersion must be <base application version>-preview.<positive workflow run number>.'
    }
    if (-not $PSBoundParameters.ContainsKey('SourceRevision')) {
        throw 'A prerelease requires an explicit SourceRevision; provenance is never inferred from missing CI inputs.'
    }
} else {
    $ReleaseVersion = $version
}
if ($PSBoundParameters.ContainsKey('SourceRevision') -and $SourceRevision -cnotmatch '^[0-9a-fA-F]{40}$') {
    throw 'SourceRevision must be an exact full 40-character hexadecimal Git commit SHA.'
}
$head = & git -C $repo rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Cannot establish source provenance from the checked-out Git revision.'
}
if ($PSBoundParameters.ContainsKey('SourceRevision') -and $SourceRevision.ToLowerInvariant() -cne $head) {
    throw 'SourceRevision does not match the checked-out HEAD; publish the requested revision, not a different checkout.'
}
$SourceRevision = $head
if ($PSBoundParameters.ContainsKey('ReleaseVersion')) {
    $changes = & git -C $repo status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the prerelease source working tree.' }
    if ($changes) { throw 'A prerelease requires a clean working tree at SourceRevision.' }
}
$variant = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$name = "WinChatCopilot-$ReleaseVersion-win-x64-$variant"
$destination = Join-Path $OutputDirectory $name
$sdkVersion = & $DotnetPath --version
if ($LASTEXITCODE -ne 0) { throw 'The required .NET SDK is unavailable.' }
& $DotnetPath publish $project --configuration Release --runtime win-x64 `
    --self-contained $SelfContained.IsPresent --output $destination --nologo `
    '-p:PublishSingleFile=false' '-p:PublishTrimmed=false'
if ($LASTEXITCODE -ne 0) { throw 'Distribution publish failed; incomplete output is retained for diagnosis.' }
if (-not (Test-Path -LiteralPath (Join-Path $destination 'WinChatCopilot.exe'))) {
    throw 'Publish did not produce the application executable.'
}
Copy-Item -LiteralPath (Join-Path $repo 'docs\user-guide.md') -Destination (Join-Path $destination 'USER-GUIDE.md')
$archive = Join-Path $OutputDirectory "$name.zip"
Compress-Archive -LiteralPath $destination -DestinationPath $archive
$manifest = [ordered]@{
    ApplicationVersion = $version
    ReleaseVersion = $ReleaseVersion
    SourceRevision = $SourceRevision
    RuntimeIdentifier = 'win-x64'
    SelfContained = $SelfContained.IsPresent
    SdkVersion = $sdkVersion
    PublishedUtc = [DateTime]::UtcNow.ToString('o')
    Archive = [IO.Path]::GetFileName($archive)
    ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Files = @(Get-ChildItem -LiteralPath $destination -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            Name = [IO.Path]::GetRelativePath($destination, $_.FullName).Replace('\', '/')
            Bytes = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
$manifestPath = Join-Path $OutputDirectory "$name.sha256.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'Test-Package.ps1') -ManifestPath $manifestPath | Out-Null
$manifest | ConvertTo-Json -Depth 5
