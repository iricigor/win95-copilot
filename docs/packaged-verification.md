# Packaged verification

Routine `dotnet build`/`dotnet test` and Windows CI remain credential-free.
The existing suites cover logic, streaming through actual synthetic processes,
exact UI-state/command regressions, export failure/snapshot/overwrite behavior,
preferences, and owned cleanup. They are not real-service or visual evidence.

## Opt-in packaged desktop run

Publish with `scripts\Publish-App.ps1`, then run the actual distribution outside
the build directory. Use a new absolute evidence directory and the solution's
built synthetic executable (all its runtime files must remain beside it):

```powershell
powershell.exe -NoProfile -STA -File .\scripts\Test-PackagedApp.ps1 `
  -RunDesktopTests `
  -ExecutablePath 'C:\builds\winchat-0.1.0\WinChatCopilot-0.1.0-win-x64\WinChatCopilot.exe' `
  -FakeCliPath 'C:\repo\tests\WinChatCopilot.FakeCli\bin\Release\net10.0\WinChatCopilot.FakeCli.exe' `
  -EvidenceDirectory 'C:\checks\winchat-new-run'
```

Use Windows PowerShell 5.1 with STA for Windows UI Automation. A non-input
desktop creation probe is recorded, then the explicitly authorized interactive
desktop is used for the test application's own windows. The harness never
switches the user's desktop. UIA elements and native handles must match the
retained run-owned process ID before interaction; keyboard input additionally
requires the verified owned foreground window. No unrelated window input is
permitted. The UIA fixture uses Copy/Paste with synthetic text, so it changes
the desktop clipboard; do not use it while relying on clipboard contents.

Each launch uses a separate settings/work directory under the selected evidence
directory. Credentials are removed from synthetic subprocess environments.
The test-only native save dialog starts there, disables Recent entries and uses
a fresh dialog-history identity instead of the normal user's dialog history.
Successful runs close every owned instance and verify owned synthetic child
exit. On failure, run-owned process handles are settled and diagnostics retained;
failure is never converted to a pass. Synthetic fixture gates have ten-second
safety bounds, and interactive waits have thirty-second bounds; these are not
production inference deadlines. Keep the evidence directory for diagnosis.

`results.json` records each completed actual interaction, failure/cleanup,
OS and executable identity, desktop probe and UI-process memory separately from
the synthetic CLI process. `WorkingSetBytes` is resident working set and
`PrivateBytes` is process-private committed memory, sampled at the named
workload/time; no hard memory threshold is applied. PNGs use `PrintWindow`
against verified owned HWNDs, not full-desktop screenshots. Active/inactive
title-color samples are limited checks, not pixel-identical visual acceptance.
The output does not store real prompts, credentials, or user conversations.

## Acceptance mapping

| Epic requirement | Evidence |
|---|---|
| Reference-led chrome, About/version, normal sizing | Shell/scaffold tests; published active/inactive/About captures; actual title buttons, native caption double-click, system Move/Size, minimum-size layout and About modal Enter/Escape |
| File/Edit/Options/Help and keyboard | Actual menus, mnemonic smoke, Copy/Paste, settings, save, reset, shortcut send modes and closing paths; focused command regressions |
| Discovery/invalid configuration/authentication | Discovery/settings regressions; published explicit-path validation and synthetic Connect; separate real readiness/service attempt |
| Text-only streaming/continuation/reset | Actual synthetic adapter/subprocess tests and published gated output/session argument assertions; separately opt-in real suite |
| SEND/newlines/single-flight/Stop | Published keyboard/newline, busy input/SEND guards, cancellation marker/input and owned settlement; deterministic modifier/empty/duplicate matrices |
| Progressive Unicode/no diagnostics/responsiveness | Published gated output with menus usable while busy; deterministic split UTF-8/framing/filtering tests; separate real progressive evidence |
| Scrolling/wrap/timestamps | Published populated scroll-away/return, saved wrap/timestamp toggles and actual timestamp text; exact deterministic layout/follow regressions |
| Failures and no automatic duplicate prompts | Published synthetic partial failure/modal/input/marker, request-count assertion and explicit Connect; deterministic launch/auth/error cases |
| UTF-8 transcript/export | Actual native save/path/cancel; exact deterministic empty/multiline/Unicode/timestamps/state/snapshot/overwrite/cancel/write-failure tests |
| Preferences/restart | Published restart with persisted values, Auto-connect and empty new conversation; corrupt/locked/unreadable settings regressions |
| Shutdown/ownership | Published File/Alt+F4/system Close/system-box double-click and active generation with actual synthetic child; deterministic unrelated sentinel tests |

Caption dragging, detailed popup pixel/typography comparison, genuine
authentication/mid-stream service failures and provider stall behavior must not
be inferred from proxies. Record attempted and remaining
checks truthfully in the revision-specific Epic evidence note. The production
watchdog and genuine service-failure investigations remain consciously deferred,
not silently implemented as part of packaging.

## Separate real-service attempt

```powershell
.\scripts\Test-RealCopilot.ps1 -RunRealTests -ExpectedAccount YOUR_GITHUB_LOGIN
```

The desktop harness also accepts `-RunRealTests -CopilotPath 'C:\actual\copilot.exe'`
alongside `-RunDesktopTests` to exercise actual published production readiness,
SEND/context, progressive Stop and Clear Window during real streaming, then fresh
context isolation. It reports real Copilot process memory separately from UI
memory. This additional option preserves existing authentication in the real
test application's child environment, never inspecting credentials.

The standalone real suite uses existing authentication without reading/changing credentials.
It verifies the actual bundled 1.0.81 executable, bounded real streaming,
multiline/Unicode, multi-turn context/reset, zero-tool evidence, explicit
argument failure and owned cancellation/continuation. An authenticated readiness
attempt through the production adapter and an owned published-app Connect/SEND
attempt should also be recorded for final delivery. Synthetic checks never
substitute for either. Missing access after a bounded attempt is a non-blocking
evidence gap, not a passed test or a reason to obtain credentials.

Save/read back final task/PR/full-revision evidence and exact limitations on
Epic #1 before delivery. Report applicable final CI/reviews independently.
Human sign-off is optional, not required. Actual failures, unsafe behavior,
missing implementation inputs, permissions and required gates remain blockers.
