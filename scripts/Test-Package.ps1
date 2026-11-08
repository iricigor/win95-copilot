#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $ManifestPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($ManifestPath)) { throw 'ManifestPath must be absolute.' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.ApplicationVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    ($manifest.ReleaseVersion -cne $manifest.ApplicationVersion -and
     $manifest.ReleaseVersion -cnotmatch "^$([regex]::Escape($manifest.ApplicationVersion))-preview\.[1-9][0-9]*$")) {
    throw 'Invalid application/release version mapping.'
}
if ($manifest.SourceRevision -cnotmatch '^[0-9a-f]{40}$' -or $manifest.RuntimeIdentifier -cne 'win-x64' -or
    $manifest.SelfContained -isnot [bool] -or [string]::IsNullOrWhiteSpace($manifest.SdkVersion)) {
    throw 'Invalid source/runtime/SDK provenance.'
}
$published = if ($manifest.PublishedUtc -is [DateTime]) {
    [DateTimeOffset]::new($manifest.PublishedUtc)
} else {
    [DateTimeOffset]::Parse($manifest.PublishedUtc, [Globalization.CultureInfo]::InvariantCulture)
}
if ($published.Offset -ne [TimeSpan]::Zero) { throw 'PublishedUtc must represent UTC.' }
$variant = if ($manifest.SelfContained) { 'self-contained' } else { 'framework-dependent' }
$name = "WinChatCopilot-$($manifest.ReleaseVersion)-win-x64-$variant"
if ($manifest.Archive -cne "$name.zip" -or [IO.Path]::GetFileName($ManifestPath) -cne "$name.sha256.json") {
    throw 'Archive and manifest names do not match the release/runtime variant.'
}
$archivePath = Join-Path (Split-Path $ManifestPath) $manifest.Archive
if ($manifest.ArchiveSha256 -cnotmatch '^[0-9A-Fa-f]{64}$' -or
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ine $manifest.ArchiveSha256) {
    throw 'Archive SHA-256 does not match the manifest.'
}
$files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.Files) {
    if ($file.Name -isnot [string] -or $file.Name -match '(^/|\\|:|(^|/)\.\.?(/|$)|//|/$)' -or
        [string]::IsNullOrWhiteSpace($file.Name) -or ($file.Bytes -isnot [long] -and $file.Bytes -isnot [int]) -or
        $file.Bytes -lt 0 -or $file.Sha256 -cnotmatch '^[0-9A-Fa-f]{64}$') {
        throw 'Invalid packaged file path, size or SHA-256.'
    }
    if (-not $files.TryAdd($file.Name, $file)) { throw "Duplicate packaged file: $($file.Name)" }
}
foreach ($required in @('WinChatCopilot.exe', 'WinChatCopilot.dll', 'WinChatCopilot.deps.json',
                         'WinChatCopilot.runtimeconfig.json', 'USER-GUIDE.md')) {
    if (-not $files.ContainsKey($required)) { throw "Missing required packaged file: $required" }
}
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (-not $entry.FullName.StartsWith("$name/", [StringComparison]::Ordinal)) {
            throw "Unexpected archive root: $($entry.FullName)"
        }
        $relative = $entry.FullName.Substring($name.Length + 1)
        if (-not $files.ContainsKey($relative) -or -not $seen.Add($relative)) {
            throw "Unexpected or duplicate archive file: $relative"
        }
        $file = $files[$relative]
        $stream = $entry.Open()
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $algorithm.Dispose(); $stream.Dispose() }
        if ($entry.Length -ne $file.Bytes -or $hash -ine $file.Sha256) {
            throw "Packaged file size/SHA-256 mismatch: $relative"
        }
    }
    if ($seen.Count -ne $files.Count) { throw 'The archive omits manifest-listed files.' }
    $configEntry = $zip.GetEntry("$name/WinChatCopilot.runtimeconfig.json")
    $reader = [IO.StreamReader]::new($configEntry.Open())
    try { $config = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    $options = $config.runtimeOptions
    $frameworkProperty = if ($manifest.SelfContained) { 'includedFrameworks' } else { 'frameworks' }
    $opposite = if ($manifest.SelfContained) { 'frameworks' } else { 'includedFrameworks' }
    if (-not $options.PSObject.Properties[$frameworkProperty] -or $options.PSObject.Properties[$opposite] -or
        $options.tfm -cne 'net10.0' -or
        -not @($options.$frameworkProperty | Where-Object { $_.name -ceq 'Microsoft.WindowsDesktop.App' -and $_.version -match '^10\.' }).Count) {
        throw 'Runtime configuration does not match the declared .NET 10 Desktop deployment variant.'
    }
    foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll')) {
        if ($files.ContainsKey($runtimeFile) -ne $manifest.SelfContained) {
            throw "Runtime payload does not match SelfContained: $runtimeFile"
        }
    }
} finally { $zip.Dispose() }
[pscustomobject]@{ ManifestPath = $ManifestPath; Archive = $archivePath; FilesVerified = $files.Count; Verified = $true }
