#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $ValidatedRevision,
    [Parameter(Mandatory)] [string] $ValidationResult,
    [Parameter(Mandatory)] [string] $PackagingResult
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Prerelease.psm1') -Force
$event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
$decision = Get-PrereleaseDecision -EventName $env:GITHUB_EVENT_NAME -Event $event `
    -Revision $env:GITHUB_SHA -RepositoryDirectory (Split-Path $PSScriptRoot)
if (-not $decision.Eligible) { Write-Output "No publication: $($decision.Reason)"; return }
if ($ValidationResult -cne 'success' -or $PackagingResult -cne 'success' -or
    $ValidatedRevision -cne $env:GITHUB_SHA) { throw 'Publication requires successful same-SHA validation and packaging.' }
if ($env:GITHUB_REPOSITORY -cne 'iricigor/win95-copilot' -or
    [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'Repository-scoped GitHub Actions authentication is required.' }
[xml] $properties = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot) 'Directory.Build.props') -Raw
$api = {
    param($Method, $Path, $Body, $UploadFile, $DownloadFile)
    $headers = @{
        Authorization = "Bearer $env:GH_TOKEN"
        Accept = if ($DownloadFile) { 'application/octet-stream' } else { 'application/vnd.github+json' }
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $hostName = if ($UploadFile) { 'https://uploads.github.com' } else { 'https://api.github.com' }
    $parameters = @{
        Uri = "$hostName$Path"; Method = $Method; Headers = $headers
        SkipHttpErrorCheck = $true; MaximumRetryCount = 0
    }
    if ($UploadFile) { $parameters.InFile = $UploadFile; $parameters.ContentType = 'application/octet-stream' }
    elseif ($Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 8 -Compress; $parameters.ContentType = 'application/json' }
    if ($DownloadFile) { $parameters.OutFile = $DownloadFile; $parameters.PassThru = $true }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -eq 404 -and $Method -ceq 'GET' -and $Path -match '/git/ref/tags/') { return $null }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "GitHub $Method $Path failed (HTTP $($response.StatusCode)); draft/diagnostics retained. Rerun only after resolving the cause."
    }
    if (-not $DownloadFile -and $response.Content) { return ($response.Content | ConvertFrom-Json) }
}
Publish-VerifiedPrerelease -Repository $env:GITHUB_REPOSITORY -Revision $env:GITHUB_SHA `
    -Version ([string] $properties.Project.PropertyGroup.Version) -RunNumber $env:GITHUB_RUN_NUMBER `
    -PackageDirectory $PackageDirectory -Api $api
