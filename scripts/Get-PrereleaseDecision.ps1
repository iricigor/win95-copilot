#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Prerelease.psm1') -Force
$event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
$decision = Get-PrereleaseDecision -EventName $env:GITHUB_EVENT_NAME -Event $event `
    -Revision $env:GITHUB_SHA -RepositoryDirectory (Split-Path $PSScriptRoot)
$decision | ConvertTo-Json -Depth 3
if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { throw 'GITHUB_OUTPUT is required.' }
Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "eligible=$($decision.Eligible.ToString().ToLowerInvariant())" -Encoding utf8
Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "revision=$env:GITHUB_SHA" -Encoding utf8
