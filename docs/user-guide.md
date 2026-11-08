# WinChat-Copilot 0.1

A Windows 3.1-style, text-only Copilot chat application. This is a modern
64-bit Windows application, not a 16-bit program. Prompts may be sent to
Copilot's remote service. It does not edit files, run shell tools, store
credentials, or restore conversations after restart.

## Install and run

Extract the complete `WinChatCopilot-<version>-win-x64.zip` into a writable
location of your choice and run `WinChatCopilot.exe`. Keep all extracted files
together. No installer, administrator access, registry registration, store
publication, or signing is provided. Windows may warn about an unsigned download;
verify the distribution's SHA-256 against its accompanying manifest.

The default framework-dependent distribution requires the **.NET 10 Desktop
Runtime (x64)**. The ordinary .NET runtime alone does not include WPF. A
self-contained distribution includes the runtime and does not require a
separate runtime installation. Both support x64 Windows 11 and Windows 10
versions listed by the [.NET Windows support policy](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md),
such as Enterprise LTSC 2021/21H2, not unsupported Windows 10 22H2.
There is no tested ARM64/x86 distribution or broad compatibility guarantee.

## Copilot prerequisites

Install Copilot CLI yourself using
[GitHub's installation instructions](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli);
the Windows WinGet command is `winget install GitHub.Copilot`. Authenticate
through the CLI's own supported login flow before using the application.
An existing Copilot entitlement and network connection are required. The
application never installs, updates, signs in, or asks for credentials.

This release supports the verified **bundled CLI 1.0.81** interface only.
Check your selected actual executable with
`copilot.exe --no-auto-update --version`. The app also disables automatic
updates in each invocation. A newer package need not bundle this version:
an incompatible installation receives an explicit error, not unrestricted
tool permissions or a silent fallback. Do not assume that ordinary `--version`
reports the bundled version when a downloaded update is present.

The app discovers the first `copilot.exe` on PATH, then documented default
WinGet package roots. Use **Options > Backend Settings** (or Settings) to select
an absolute executable path for custom installations. Blank means discovery.
A bad explicit path is never replaced by a different executable. Missing or
invalid discovery opens configuration guidance; Cancel still allows Exit.

**Connect** checks version and performs a small text-only service request.
A ready status means that check succeeded, not permanent connectivity.
Existing OS-stored authentication or supported inherited token environment
variables must be available to the process. `gh` login alone is not proof.
The isolated CLI home intentionally does not reuse ordinary CLI conversations
or configuration. Authentication failures explain that login should be
completed separately; the app does not automatically retry.

## Chat, menus, and preferences

SEND and the selected shortcut submit the same nonempty input. The default
Ctrl+Enter mode leaves Enter/Shift+Enter available for multiline editing.
Enter mode submits on Enter, including modified Enter; paste remains available
for multiline text. SEND is disabled while unavailable or busy. Only one turn
runs at a time, and input is retained until successful completion.

While waiting for the assistant's first text, its heading cycles from
`Assistant: .` through `Assistant: .....` every half-second, then restarts.
The dots disappear on the first text (including whitespace), even if the
response is still streaming. Stop, failure, Clear Window and Exit also remove
them. They are temporary display state, never response text or saved transcript
content; saving while waiting still includes `[In progress]`.

Follow-up prompts share one backend session. **File > Clear Window** cancels
and settles active work, clears history, and prepares a different session.
Unsent/cancelled input is preserved for review, never automatically submitted.
**Stop** preserves partial output with `[Cancelled]`; failed responses have
`[Failed]` and an actionable modal error. After failure, Connect explicitly
checks the same session before the next deliberate SEND.

Edit Copy/Paste/Select All act on the focused text pane; the transcript cannot
be edited. Word Wrap applies to both panes. Show Timestamp prefixes both speaker
names with the same turn submission time in 24-hour `[HH:mm]` form, for example
`[09:05] User: ` and `[09:05] Assistant: `, without date, seconds or timezone
conversion. It is off by default; exports use the same heading format.
Scrolling away from the bottom
keeps your reading position during streaming; return to the bottom to follow.
Options persist send mode, wrap, timestamps, Auto-connect and executable path.
Auto-connect is off by default and can be enabled after configuration.

**Options > User Name...** sets the user heading (default `User`) in a retro
dialog. OK trims outer whitespace and accepts Unicode and interior spaces;
blank names, line breaks (including Unicode line/paragraph separators), tabs
and control characters show inline guidance. There is no arbitrary short name
limit. Cancel/Escape changes nothing; a failed save keeps the previous name and
shows an actionable error without closing the dialog. The saved name survives
restart and unrelated preference/backend changes.

A successful save immediately renames every current user heading, including
during streaming, and subsequent headings and exports. `Assistant` stays fixed;
this changes presentation only, never prompts, authentication or conversation
identity. Selection and reading position are preserved. Very long names retain
their full text and use ordinary wrapping/horizontal scrolling within WPF's
native layout limits.

**File > Save Transcript** uses the native save/overwrite dialog and writes
UTF-8 without a byte-order mark. Export includes speaker labels, multiline
text, selected timestamps and explicit failed/cancelled/in-progress markers.
It captures a snapshot before the dialog; later streamed text or display-name
changes are not included.
Cancel writes nothing, and write failures leave the conversation intact.

Help/About shows the application version. The left system box provides normal
window actions; title controls minimize/maximize/restore, without a modern X.
File Exit, system Close, system-box double-click and Alt+F4 settle owned work.

## Local data and limitations

Preferences normally live at `%LOCALAPPDATA%\WinChatCopilot\settings.json`,
with app-isolated `cli` and `work` directories beside them. The app does not
save authentication. For automated tests only, `WINCHAT_DATA_DIRECTORY` selects
an absolute isolated directory; never point it at real user data.
Deleting an installation folder does not delete local preferences.

Cancellation stops owned local work; it does not promise immediate cessation
of remote inference/billing or persistence of every partial response.
There is **no production stall watchdog or provider latency guarantee**.
A silent request remains busy until Stop or Exit. Real signed-out/mid-stream
service-failure/post-failure evidence remains consciously deferred under Epic #1.
Synthetic failure tests do not substitute for that evidence. Native Windows
save/open dialogs and normal OS task switching are not Windows 3.1 emulations.
The reference-led scalable WPF styling is not pixel-identical 640x480 emulation.

## Build a distribution

Use a supported .NET 10 SDK and PowerShell 7 on Windows. From the repository:

```powershell
.\scripts\Publish-App.ps1 -OutputDirectory 'C:\builds\winchat-0.1.0'
# Optional: include the runtime; otherwise install .NET 10 Desktop Runtime x64.
.\scripts\Publish-App.ps1 -OutputDirectory 'C:\builds\winchat-0.1.0-self-contained' -SelfContained
```

Select a new absolute output directory; the script refuses to overwrite one.
It publishes Release/win-x64 without trimming or single-file extraction, copies
this guide, produces a zip, and records versions and file/archive SHA-256 hashes
in `manifest.json`. It does not install Copilot or alter system settings.
Use `-DotnetPath` for an explicitly selected SDK. The application version comes
from `Directory.Build.props`, not this guide's release-family heading.
