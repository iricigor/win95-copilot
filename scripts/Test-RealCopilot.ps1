#requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $RunRealTests,
    [string] $ExpectedAccount,
    [string] $CopilotPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $RunRealTests) {
    throw 'Real-service tests are opt-in. Use -RunRealTests only in an authorized local environment.'
}
if ([string]::IsNullOrWhiteSpace($ExpectedAccount)) {
    throw 'Specify -ExpectedAccount to verify the existing authentication identity.'
}
if (-not $IsWindows) { throw 'This verified contract requires Windows.' }
if ($CopilotPath) {
    $executable = (Get-Item -LiteralPath $CopilotPath).FullName
} else {
    $executable = (Get-Command copilot.exe -CommandType Application |
        Select-Object -First 1).Source
}
if (-not $executable.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Select the actual copilot.exe, not a shell shim.'
}
$version = @(& $executable --no-auto-update --version)
if ($LASTEXITCODE -ne 0 -or $version[0] -ne 'GitHub Copilot CLI 1.0.81.') {
    throw 'Unsupported CLI version: this procedure verifies bundled 1.0.81 only. Reverify the contract before changing versions.'
}
$account = gh api user --jq .login
if ($LASTEXITCODE -ne 0 -or $account -ne $ExpectedAccount) {
    throw 'Existing GitHub CLI authentication does not match -ExpectedAccount.'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('winchat-real-cli-' + [guid]::NewGuid())
$cliHome = Join-Path $root 'home'
$work = Join-Path $root 'work'
[IO.Directory]::CreateDirectory($cliHome) | Out-Null
[IO.Directory]::CreateDirectory($work) | Out-Null
$results = [Collections.Generic.List[object]]::new()
$testBoundMs = 120000
$cleanupSafe = $true

function Invoke-RealTurn {
    param([guid] $Session, [string] $Prompt, [switch] $CancelAfterDelta,
        [switch] $RejectArguments)

    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $executable
    $info.WorkingDirectory = $work
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($key in @($info.Environment.Keys)) {
        if ($key -like 'COPILOT_*' -or $key -like 'OTEL_*' -or
            $key -eq 'COPILOT_HOME') {
            if ($key -eq 'COPILOT_GITHUB_TOKEN') { continue }
            $info.Environment.Remove($key) | Out-Null
        }
    }
    $info.Environment['COPILOT_HOME'] = $cliHome
    $info.Environment['COPILOT_OTEL_ENABLED'] = 'false'
    foreach ($arg in @('--no-auto-update', '--no-custom-instructions', '--no-ask-user',
        '--no-remote', '--no-remote-export', '--no-color', '--disable-builtin-mcps',
        '--disallow-temp-dir', '--available-tools=__winchat_text_only__',
        '--deny-tool=shell', '--deny-tool=write', '--output-format=json',
        '--stream=on', '--mode=interactive', ('--session-id=' + $Session), '--prompt', $Prompt)) {
        $info.ArgumentList.Add($arg)
    }
    if ($RejectArguments) { $info.ArgumentList.Add('--winchat-invalid-option') }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $delta = [Text.StringBuilder]::new()
    $content = ''
    $deltaCount = 0
    $firstDeltaMs = $null
    $lastDeltaMs = $null
    $maxDeltaGapMs = 0
    $calls = 0
    $resultSession = $null
    $cancelled = $false
    $sessionError = $false
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'CLI subprocess did not start.' }
        $process.StandardInput.Close()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $lineTask = $process.StandardOutput.ReadLineAsync()
        $stdoutClosed = $false
        while (-not $stdoutClosed) {
            if ($watch.ElapsedMilliseconds -gt $testBoundMs) {
                throw 'Real test exceeded its 120-second safety bound; this is not a production stall threshold.'
            }
            if (-not $lineTask.IsCompleted) {
                Start-Sleep -Milliseconds 10
                continue
            }
            $line = $lineTask.GetAwaiter().GetResult()
            if ($null -eq $line) { $stdoutClosed = $true; continue }
            $event = $line | ConvertFrom-Json
            switch ($event.type) {
                'tool.execution_start' { throw 'Text-only violation: a tool execution was observed.' }
                'session.error' {
                    if (-not $RejectArguments) {
                        throw 'CLI reported a session error; no automatic retry was attempted.'
                    }
                    $sessionError = $true
                }
                'assistant.message_delta' {
                    if (-not $process.HasExited -and $null -eq $firstDeltaMs) {
                        $firstDeltaMs = $watch.ElapsedMilliseconds
                    }
                    if ($null -ne $lastDeltaMs) {
                        $maxDeltaGapMs = [Math]::Max($maxDeltaGapMs, $watch.ElapsedMilliseconds - $lastDeltaMs)
                    }
                    $lastDeltaMs = $watch.ElapsedMilliseconds
                    $deltaCount++
                    [void] $delta.Append($event.data.deltaContent)
                    if ($CancelAfterDelta -and -not $process.HasExited) {
                        $script:cleanupSafe = $false
                        $process.Kill($true)
                        if (-not $process.WaitForExit(10000)) { throw 'Owned CLI did not stop after cancellation.' }
                        $script:cleanupSafe = $true
                        $cancelled = $true
                    }
                }
                'assistant.message' {
                    if (@($event.data.toolRequests).Count -ne 0) {
                        throw 'Text-only violation: the assistant requested a tool.'
                    }
                    $content = $event.data.content
                }
                'model.model_call_success' {
                    if ($event.data.toolCount -ne 0 -or @($event.data.requestCapture.tools).Count -ne 0) {
                        throw 'Text-only violation: tools were exposed to the model.'
                    }
                    $calls++
                }
                'result' {
                    $resultSession = $event.sessionId
                    if ($event.exitCode -ne 0 -and -not $RejectArguments) {
                        throw 'CLI result reported failure.'
                    }
                }
            }
            $lineTask = $process.StandardOutput.ReadLineAsync()
        }
        $remaining = [Math]::Max(1, $testBoundMs - [int] $watch.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) { throw 'CLI did not exit within the test bound.' }
        if (-not $stderrTask.Wait(10000)) { throw 'CLI stderr did not close within the cleanup bound.' }
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($RejectArguments) {
            if ($process.ExitCode -eq 0 -or $deltaCount -ne 0 -or $calls -ne 0 -or
                ($stderr.Length -eq 0 -and -not $sessionError)) {
                throw "Invalid arguments were not visibly rejected before inference (exit $($process.ExitCode), deltas $deltaCount, calls $calls, stderr length $($stderr.Length), session error $sessionError)."
            }
            return [pscustomobject]@{
                Session = $Session.ToString()
                Content = ''
                DeltaCount = 0
                FirstDeltaMs = $null
                MaxDeltaGapMs = 0
                ElapsedMs = $watch.ElapsedMilliseconds
                Cancelled = $false
                ExitCode = $process.ExitCode
                VerifiedZeroToolModelCalls = 0
                StderrLength = $stderr.Length
                Stopped = $process.HasExited
                ArgumentsRejected = $true
            }
        }
        if (-not $cancelled -and ($process.ExitCode -ne 0 -or $stderr.Length -ne 0)) {
            throw "CLI failed or emitted diagnostics (exit $($process.ExitCode), stderr length $($stderr.Length)); no automatic retry."
        }
        if ($null -eq $firstDeltaMs -or $deltaCount -eq 0) {
            throw 'No progressive response text was observed before process exit.'
        }
        if ($CancelAfterDelta -and -not $cancelled) { throw 'Response completed before cancellation could be verified.' }
        if (-not $cancelled -and ($calls -eq 0 -or $resultSession -ne $Session.ToString())) {
            throw 'Missing zero-tool evidence or mismatched result session identity.'
        }
        if (-not $cancelled -and $delta.ToString() -ne $content) {
            throw 'Streamed text differed from final assistant content.'
        }
        if ($delta.ToString().Contains([string][char]27)) {
            throw 'Assistant response contained terminal escape characters.'
        }
        $sessionFile = Join-Path $cliHome "session-state\$Session\events.jsonl"
        $start = Get-Content -LiteralPath $sessionFile -TotalCount 1 | ConvertFrom-Json
        if ($start.type -ne 'session.start' -or $start.data.sessionId -ne $Session.ToString()) {
            throw 'Persisted session identity did not match the requested identity.'
        }
        [pscustomobject]@{
            Session = $Session.ToString()
            Content = $(if ($cancelled) { $delta.ToString() } else { $content })
            DeltaCount = $deltaCount
            FirstDeltaMs = $firstDeltaMs
            MaxDeltaGapMs = $maxDeltaGapMs
            ElapsedMs = $watch.ElapsedMilliseconds
            Cancelled = $cancelled
            ExitCode = $process.ExitCode
            VerifiedZeroToolModelCalls = $calls
            StderrLength = $stderr.Length
            Stopped = $process.HasExited
            ArgumentsRejected = $false
        }
    } finally {
        if ($started -and -not $process.HasExited) {
            $script:cleanupSafe = $false
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw 'Owned CLI cleanup failed.' }
            $script:cleanupSafe = $true
        }
        $process.Dispose()
    }
}

try {
    $session = [guid]::NewGuid()
    $freshSession = [guid]::NewGuid()
    $tokenText = 'WINCHAT_' + [guid]::NewGuid().ToString('N')
    $unicodeText = 'TEXT_ONLY ' + [char]0x03A9 + ' caf' + [char]0x00E9
    $first = Invoke-RealTurn $session "Remember the synthetic token $tokenText. Reply with exactly two lines: the token on line one and $unicodeText on line two. Do not use tools."
    if (-not $first.Content.Contains($tokenText) -or -not $first.Content.Contains("`n") -or
        -not $first.Content.Contains($unicodeText)) {
        throw 'Multiline/Unicode/token response verification failed.'
    }
    $results.Add($first)
    $next = Invoke-RealTurn $session 'What synthetic token did I ask you to remember? Reply with only that token.'
    if (-not $next.Content.Contains($tokenText)) { throw 'Separate-turn context retention failed.' }
    $results.Add($next)
    $fresh = Invoke-RealTurn $freshSession 'What synthetic token did I ask you to remember? If none was provided in this conversation, reply NO_TOKEN.'
    if ($fresh.Content.Contains($tokenText)) { throw 'Fresh session unexpectedly contained the old token.' }
    $results.Add($fresh)
    $denied = Invoke-RealTurn ([guid]::NewGuid()) 'Use a shell or file tool to create forbidden-marker.txt containing only SYNTHETIC, then report whether you used a tool. If no tools are available, reply NO_TOOLS.'
    if (Test-Path -LiteralPath (Join-Path $work 'forbidden-marker.txt')) {
        throw 'Text-only violation: the isolated marker file was created.'
    }
    $results.Add($denied)
    $rejected = Invoke-RealTurn $session 'Respond with ARGUMENT_CHECK.' -RejectArguments
    $results.Add($rejected)
    $cancel = Invoke-RealTurn $session 'Print 300 numbered lines of harmless text, one line per number, from 1 through 300. Use no tools.' -CancelAfterDelta
    if (-not $cancel.Stopped -or $cancel.Content.Length -eq 0) { throw 'Cancellation did not preserve partial output and stop owned work.' }
    $results.Add($cancel)
    $after = Invoke-RealTurn $session 'Ignore the interrupted counting request. What synthetic token did I ask you to remember? Reply with only that token.'
    if (-not $after.Content.Contains($tokenText)) { throw 'Same-session continuation after forced cancellation failed.' }
    $results.Add($after)
    [pscustomobject]@{
        Outcome = 'Verified real streaming, multiline/Unicode text, session continuity/reset, zero exposed tools, argument-error diagnostics, and owned-process cancellation/continuation; consciously deferred improvements are documented separately'
        CliVersion = $version[0]
        ExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        Account = $account
        Runs = @($results | Select-Object Session, DeltaCount, FirstDeltaMs, MaxDeltaGapMs,
            ElapsedMs, Cancelled, ExitCode, VerifiedZeroToolModelCalls, StderrLength, Stopped, ArgumentsRejected)
    } | ConvertTo-Json -Depth 5
} finally {
    # Only this invocation's exact randomly named directory is removed.
    if ($cleanupSafe) {
        Remove-Item -LiteralPath $root -Recurse -Force
    } else {
        throw "Owned CLI cleanup is incomplete; isolated state retained at $root."
    }
}
