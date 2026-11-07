# win95-copilot

## Application scaffold

The bootstrap uses **WPF on .NET 10 LTS**. WPF supplies a lightweight,
unpackaged Windows executable, XAML controls, and ordinary Windows keyboard and
window behavior without requiring the Windows App SDK. Its styling and window
customization support the later Windows 3.1 shell task. The current window is
only a placeholder with native modern chrome, not a claim of retro fidelity.
There are no menus, chat features, Copilot processes, or service calls yet.

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

The run command displays the placeholder window and the application version in
its title. Close it with the normal window close control or Alt+F4; the process
should exit. This is the minimal manual launch/close check, not the future
Windows 3.1 appearance checklist. Do not automate a user's working desktop.
The scaffold tests load window resources on a dedicated STA thread without
showing a window and verify the displayed version against generated metadata.
No CI or fake-CLI harness is installed by this bootstrap; Epic child #6 owns
those validation mechanisms.

### Project layout and version

`src\WinChatCopilot` contains the WPF entry point and placeholder window.
`tests\WinChatCopilot.Tests` contains xUnit tests with a reference to the app;
later tasks can add UI-independent logic and actual subprocess tests without a
separate speculative service architecture.

**Application version has one source:** `Version` in `Directory.Build.props`.
MSBuild generates assembly/file/informational metadata from it; the window uses
that metadata through `ApplicationInfo.Version` (without a source-commit suffix).
Change the property rather than adding a separate UI version constant.

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