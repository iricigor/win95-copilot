# win95-copilot

## Application scaffold

The bootstrap uses **WPF on .NET 10 LTS**. WPF supplies a lightweight,
unpackaged Windows executable, XAML controls, and ordinary Windows keyboard and
window behavior without requiring the Windows App SDK. Its styling and window
customization support the Windows 3.1 shell without Windows App SDK packaging.
The reference-led [Windows 3.1 shell](docs/windows31-shell.md) supplies menus,
text areas, action surfaces and a versioned About dialog. Chat features, Copilot
processes and service calls are not implemented yet; their actions remain
visibly disabled with a truthful unavailable status.

### Prerequisites

- Windows 10 (for example, Enterprise LTSC 2021/21H2) or Windows 11, on a version supported by the
  [.NET Windows OS policy](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
  Ordinary Windows 10 22H2 is no longer listed as supported by .NET 10.
  .NET does not extend the operating system's lifecycle.
- The latest serviced **.NET 10 SDK** for development and tests. `global.json`
  accepts stable 10.0 SDK feature bands starting at 10.0.100.
- The **.NET 10 Desktop Runtime** for running a framework-dependent build on a
  machine without the SDK. The SDK includes the development/runtime components.
- Internet access to NuGet for the first test dependency restore. Copilot
  installation, authentication, and inference access are not required.

.NET 10 LTS is supported through November 14, 2028 under Microsoft's
[support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core);
keep SDK/runtime patches current. .NET 8 was not selected because its support
ends November 10, 2026. WPF is Windows-only. There is no hard 50 MB memory gate;
UI-process and backend-process memory will be measured separately in the final
packaging task.

### Local commands

Run these PowerShell commands from the repository root:

```powershell
dotnet restore .\WinChatCopilot.sln
dotnet build .\WinChatCopilot.sln --configuration Release --no-restore
dotnet test .\WinChatCopilot.sln --configuration Release --no-build --no-restore
dotnet run --project .\src\WinChatCopilot\WinChatCopilot.csproj --configuration Release --no-build --no-restore
```

The run command displays the retro shell and the application version in its
title. Close it with File > Exit, system-menu Close, or Alt+F4; there is no
dedicated X button. Follow the [local reference/appearance checklist](docs/windows31-shell.md)
for actual desktop acceptance. Do not automate a user's working desktop.
The scaffold tests load window resources on a dedicated STA thread without
showing a window and verify the displayed version against generated metadata.
The deterministic fake-CLI suite and Windows CI are described below. Neither
requires Copilot installation, authentication, or service access.

### Project layout and version

`src\WinChatCopilot` contains the WPF entry point, shared retro window/control
resources, shell commands, main window and About dialog.
`tests\WinChatCopilot.Tests` contains xUnit tests with a reference to the app;
later tasks can add UI-independent logic and actual subprocess tests without a
separate speculative service architecture.

**Application version has one source:** `Version` in `Directory.Build.props`.
MSBuild generates assembly/file/informational metadata from it; the window uses
that metadata through `ApplicationInfo.Version` (without a source-commit suffix).
Change the property rather than adding a separate UI version constant.

## Deterministic process tests and Windows CI

The same restore/build/test commands above run scaffold checks and the
`ProcessTransportTests` suite. The solution builds the small
`tests\WinChatCopilot.FakeCli` console executable and copies its apphost/runtime
files into the test output's `Fake CLI` directory (intentionally containing a
space). Tests launch that actual executable through production
`src\WinChatCopilot\Processes\ProcessTransport.cs`, never a mocked transport,
shell, or real `copilot.exe`. Fixtures use synthetic prompts/session IDs and
unique temporary working directories; helper PATH is empty and credential
environment variables are removed.

The transport uses `ArgumentList`, closed stdin and independently drained,
strict UTF-8 stdout/stderr. Callbacks receive raw character chunks, not lines;
concatenation preserves multiline/Unicode text even when UTF-8 bytes arrive
separately. Callbacks on the two streams can overlap and must not block or
assume cross-stream ordering. Nonzero OS exit codes are returned explicitly;
launch, encoding, callback and cleanup errors propagate. Caller cancellation
throws only after owned work settles, preserving chunks already delivered.
Terminal escape sequences are deliberately still raw here: the later CLI
adapter owns response framing, diagnostic filtering and terminal sanitization,
and must reuse this exercised transport rather than create another one.

Scenarios cover gated progressive output without newline boundaries, split
UTF-8/multiline output, stdout/stderr and terminal formatting separation,
stderr backpressure, clean/nonzero/partial-failure exits, silent stalls, cancellation before/after
output, an owned child and an unrelated sentinel, invalid encoding and failed
consumers. Argument/session assertions round-trip quotes, metacharacters,
newlines, Unicode, empty arguments and trailing backslashes. Deliberately
incorrect session/prompt scenarios demonstrate that these assertions fail.
These are synthetic transport tests, **not real Copilot compatibility,
authentication, persisted session history, or UI behavior evidence**.

Each fixture has a 30-second test safety bound, readiness gates have ten-second
bounds, and cancellation/process-tree exit has a ten-second cleanup bound.
Tests explicitly await termination and check owned parent/child handles while
an unrelated sentinel remains alive. Stalled helpers also self-expire after
30 seconds in their stall, with separate ten-second readiness gates;
fixture teardown cancels pending work and deletes only its
unique temporary directory. These are runner/cleanup bounds, **not a production
inference timeout policy**. Tree termination uses .NET's owned process handle
while the parent is alive; detached/reparented children are not a guaranteed
supported backend contract. Do not use process-name kills.

`.github\workflows\windows-tests.yml` runs these commands for PRs and `main`
pushes on `windows-latest` with .NET 10, read-only repository permissions, a
ten-minute job bound, and two-minute test-host hang diagnostics. The test step
removes Copilot from PATH and fails if it is still discoverable. Results are
uploaded even on failure. It never calls the opt-in real smoke script or passes
Copilot credentials. To reproduce that extra absence/hang check locally in a
fresh PowerShell process after restore/build:

```powershell
$dotnet = (Get-Command dotnet.exe -CommandType Application | Select-Object -First 1).Source
$env:PATH = "$(Split-Path $dotnet);$env:SystemRoot\System32;$env:SystemRoot"
if (Get-Command copilot.exe -ErrorAction SilentlyContinue) { throw 'Copilot is still on PATH.' }
& $dotnet test .\WinChatCopilot.sln --configuration Release --no-build --no-restore --logger trx --results-directory .\TestResults --blame-hang --blame-hang-timeout 2m
```

Later features add focused application tests and extend this executable with
explicit synthetic scenarios (and negative assertions) as needed. Keep
filesystem fixtures isolated, release output gates or cancel in teardown, and
assert exact output/state rather than assuming successful process exit is
feature acceptance. Visual/desktop checks remain manual; real-Copilot tests
remain separately opt-in and report missing prerequisites as unavailable.

## Opt-in real CLI investigation

The [Copilot CLI contract investigation](docs/copilot-cli-contract.md) records
real streaming/session/text-only/cancellation evidence for the tested bundled
CLI and consciously deferred stall/error improvements agreed on Epic #1. The application scaffold
does not yet integrate Copilot. `scripts\Test-RealCopilot.ps1` requires an explicit
real-service opt-in and existing local authentication; it is not a routine CI
test; a passing smoke run does not claim the deferred improvements or the full
application are implemented.

## Standalone 💾 95 Developer

The repository's [💾 95 Developer](.github/agents/epic-developer.agent.md) follows
the standalone worker model: each authorized run delivers at most one eligible
native child of [Epic #1](https://github.com/iricigor/win95-copilot/issues/1),
including implementation, validation, repair, and a verified merge to `main`.
It does not need a Dispatcher. No child tasks means the run stops without changes;
the worker does not create a breakdown or implement the entire Epic.

Select **💾 95 Developer** in a fresh isolated worktree and use this kickoff:

```text
Read docs\automation-prompts\epic-worker.md and execute its instructions.
```

The same kickoff supports the user-approved hourly local Copilot app Automation.
The schedule, selected repository agent, enabled state, and execution environment
are app configuration, not automatically installed by these Markdown files.
Scheduled runs require the computer and Copilot app to be available, and fresh
worktrees must contain these files on `main`.

Any unfinished repository PR or retained worker claim blocks new task pickup.
Do not overlap manual invocations with scheduled runs. Claims and draft PRs are
visibility guards, not atomic locks; interrupted or ambiguous work needs explicit
human recovery rather than automatic takeover on the next hourly tick.

Each claimed run records task diagnostics and a brief Epic outcome. Merging
requires acceptance evidence and all applicable current-revision checks/reviews,
without changing protection, validation, or release settings. Missing permissions
or evidence leave the task visibly blocked or incomplete.