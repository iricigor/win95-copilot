#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $DotnetPath = 'dotnet',
    [switch] $SelfContained
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
$name = "WinChatCopilot-$version-win-x64"
$destination = Join-Path $OutputDirectory $name
$sdkVersion = & $DotnetPath --version
if ($LASTEXITCODE -ne 0) { throw 'The required .NET SDK is unavailable.' }
& $DotnetPath publish $project --configuration Release --runtime win-x64 `
    --self-contained $SelfContained.IsPresent --output $destination --nologo `
    -p:PublishSingleFile=false -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw 'Distribution publish failed; incomplete output is retained for diagnosis.' }
if (-not (Test-Path -LiteralPath (Join-Path $destination 'WinChatCopilot.exe'))) {
    throw 'Publish did not produce the application executable.'
}
Copy-Item -LiteralPath (Join-Path $repo 'docs\user-guide.md') -Destination (Join-Path $destination 'USER-GUIDE.md')
$archive = Join-Path $OutputDirectory "$name.zip"
Compress-Archive -LiteralPath $destination -DestinationPath $archive
$manifest = [ordered]@{
    ApplicationVersion = $version
    RuntimeIdentifier = 'win-x64'
    SelfContained = $SelfContained.IsPresent
    SdkVersion = $sdkVersion
    PublishedUtc = [DateTime]::UtcNow.ToString('o')
    Archive = [IO.Path]::GetFileName($archive)
    ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    Files = @(Get-ChildItem -LiteralPath $destination -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ Name = $_.Name; Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8NoBOM
$manifest | ConvertTo-Json -Depth 5
