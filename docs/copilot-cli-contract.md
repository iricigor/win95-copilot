# Real Copilot CLI contract investigation

**Status: verified contract within the user-agreed scope of task #4.** Real
subprocess access, text-only streaming, session identities, and local
cancellation were observed. On 2026-11-07, the user explicitly deferred the
stall/timeout policy and real authentication/mid-stream failure verification
as consciously not done, for potential improvements on Epic #1. They are not
blockers for #4 and are not claimed as implemented or passed. This is not a
full-product end-to-end application pass. The scoped production streaming
adapter delivered by #9 reuses this contract as described below.

## Verified executable and sources

On Windows, use the first application returned by `Get-Command copilot.exe`,
or a valid explicitly selected executable. Do not concatenate multiple PATH
matches or silently replace an invalid explicit selection.

The tested WinGet-installed executable bundles **1.0.81**, with SHA-256
`0AB7B1DDF0CCFF432C1A0D9323451F2737E5D877E88355B6094BF8C8F344D7B0`.
`--no-auto-update --version` and the persisted `session.start` event identify
that version. A default version query instead reported **1.0.99-2**:
`--no-auto-update` also bypasses newer previously downloaded versions. These
results must not be attributed to 1.0.99-2, or to the Copilot desktop app.
No installation or update command was run for this investigation.

Official references, read on 2026-11-07:

- [Installation](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli):
  Windows WinGet installation is `winget install GitHub.Copilot`; npm and direct
  release downloads are also supported. Do not invent fixed installation paths;
  package versions and locations vary. The observed WinGet package was exposed
  through PATH, so no fallback directory is established here.
- [Command reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference):
  prompt mode, explicit session IDs, JSON output, streaming, authentication.
- [Tool controls](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/allowing-tools):
  availability filters and permission rules are separate layers.
- The tested executable's `--help`, `help permissions`, `help environment`, and
  `login --help` establish its actual flags. Current web documentation is not
  evidence that every newer feature exists in this bundled build.

## Opt-in reproduction

Prerequisites: Windows, PowerShell 7+, the tested actual `copilot.exe`, an
existing signed-in `gh`, and authorized Copilot service access. No .NET SDK,
production application, or CI credentials are needed for this script.

From the repository root:

```powershell
.\scripts\Test-RealCopilot.ps1 -RunRealTests -ExpectedAccount YOUR_GITHUB_LOGIN
# Optional: use -CopilotPath 'C:\your installation\copilot.exe'
```

Without `-RunRealTests`, the script throws before discovering the CLI or calling
the service. It checks the existing GitHub API identity against
`-ExpectedAccount` and the bundled CLI version; it never logs in, installs,
updates, changes settings, or falls back to a different explicit executable.
It uses the existing `gh` credential only in child-process memory, never in
arguments, output, or saved application settings.

The procedure runs six bounded harmless inference requests and one deliberate
argument-validation failure. It creates only synthetic UUIDs/tokens, a temporary
working directory, and an isolated `COPILOT_HOME`. It does not load repository
instructions, existing conversations, plugins, MCP configuration, or provider
configuration from the user's CLI home. No private repository data is included
in prompts. The explicit GitHub credential is not, by itself, proof of the
inference service's selected account; see authentication limitations below.

Only this invocation's randomly named temporary directory is removed, after
owned CLI processes have exited. Output contains summarized synthetic session
IDs, timings, exit codes, and verification counts, not raw model context,
credentials, prompts, or existing user transcripts. Do not run this in routine
CI. A successful script run verifies its listed scenarios, **not every Epic
acceptance criterion or the consciously deferred improvements**.

## Invocation and text-only enforcement

The script uses `ProcessStartInfo.ArgumentList` with redirected UTF-8 stdout,
stderr, and closed stdin. It does not invoke a shell or write prompts into a
persistent stdin stream. Each turn starts a new CLI process. Relevant arguments:

```text
--no-auto-update --no-custom-instructions --no-ask-user
--no-remote --no-remote-export --no-color --disable-builtin-mcps
--disallow-temp-dir --available-tools=__winchat_text_only__
--deny-tool=shell --deny-tool=write
--output-format=json --stream=on --mode=interactive
--session-id=<synthetic UUID> --prompt <one argument containing the prompt>
```

No `--allow-all-tools`, `--allow-all`, `--yolo`, or assisted approval is used.
**An empty `--available-tools=` is unsafe on 1.0.81:** a harmless access probe
exposed 20 tools, although none executed. A nonempty allowlist containing the
deliberately nonmatching name `__winchat_text_only__` instead produced zero
available tools. This uses the documented allowlist/intersection mechanism;
it is not a model instruction to abstain from tools.

For completed model calls, the procedure verifies `toolCount == 0` and an empty
captured tool list in `model.model_call_success`, no assistant tool requests,
and no `tool.execution_start`. A prompt asking for a shell/file action could not
create its marker in the isolated working directory. The script pins the tested
bundled version; neither the empty-allowlist behavior nor this nonmatching-name
behavior may be assumed unchanged on an unverified version. A new tool with
that name, changed filter semantics, missing diagnostic evidence, or a different
executable requires re-verification before production use.

## Streaming and session evidence

Stdout is UTF-8 JSONL, not a sequence of complete response-text lines.
`assistant.message_delta.data.deltaContent` supplies progressive text;
`assistant.message.data.content` supplies final content. The deltas concatenate
to the final multiline/Unicode response. A UI must render only response text,
not `model.*`, usage, MCP, instruction, or other diagnostic events. Unknown
non-response events are not assistant messages. ESC characters in response
text are rejected by the procedure; meaningful newlines and Unicode are kept.

A terminal `result` has top-level `sessionId` and `exitCode`. It is not an
assistant message and is not guaranteed after forced termination. Always also
observe the OS exit status and stderr; EOF alone is not successful completion.
Malformed JSON, tool activity, session errors, diagnostic stderr, and a mismatch
between deltas and final text fail the smoke procedure without automatic retry.

`--session-id=<UUID>` creates that identity when absent and continues it when
present. Separate processes retained a synthetic token across turns.
Requested UUIDs matched both terminal results and the first persisted
`session-state\<UUID>\events.jsonl` event (`session.start.data.sessionId`).
An independent UUID created a different session and did not contain the old
token. Reset proof includes identity, not just a probabilistic model answer.
The isolated home persists across test turns only, then is deleted.

The first complete six-turn run observed first deltas after 5.103-6.753 seconds,
completed turns after 6.194-8.301 seconds, and empty stderr on those turns.
These measurements are workload-specific evidence, not a service latency SLA.
The repeatable script also checks multiline Unicode text and argument failures;
its final run metrics are reported on task #4.

## Cancellation, failures, authentication, and conscious deferrals

The procedure cancels after receiving a delta using `Process.Kill(true)` on its
owned process handle and waits at most ten seconds for local exit. Partial
received text survives; observed forced-cancellation exit was `-1`. A subsequent
explicit prompt on the **same** session UUID retained the earlier token. This
establishes local process-tree termination and observed continuation after
forced termination, not graceful protocol cancellation, preservation of all
unflushed partial history, or immediate termination of remote billing/work.
Never use process-name kills or assume a cancelled turn has a terminal result.
Never automatically retry an accepted prompt.

The application now exposes Stop using the exercised owned process transport.
It remains busy in Cancelling until the request and queued transcript updates
settle, keeps partial text/input with a cancelled marker, and permits an explicit
next prompt on the retained identity. Failure requires explicit Connect, which
checks version/authenticated zero-tool readiness on that same identity without
resubmitting the failed user prompt. Failed recovery does not accept a fresh
identity. Callback/request guards and one close path prevent retired updates
or repeated close requests from bypassing owned-work settlement. Clear Window's
new-session UX remains a separate task.

An intentionally unknown argument exercises actual CLI validation, a nonzero
exit, separated diagnostics, and no inference. Subsequent continuation uses the
same earlier session. This is a **pre-dispatch failure**, not a reproduced
network, authentication, or mid-stream service failure.

Official authentication prerequisites include an active Copilot entitlement
and applicable organization policy, CLI OAuth login or supported tokens, and
service connectivity. The CLI's own login flow manages credentials; the app must
not collect them. `gh api user` success alone is not a Copilot readiness check.
Real inference here succeeded using existing local authentication.

**Real authentication/mid-stream failure verification is consciously not done
for #4, by user decision.** A synthetically invalid environment token, including an
OAuth-shaped all-zero token, still returned a successful real response. It was
not a reliable signed-out fixture and is not reported as an auth-rejection pass.
Changing or deleting the user's saved credentials to force failure is out of
scope. The smoke procedure instead reports genuine failures if encountered.
Authenticated failure handling and session usability after a mid-stream service
failure remain unverified; the argument-error test does not substitute for them.

**A production stall/timeout policy is consciously not done for #4, by user
decision.** Inspected help and
references provide neither a response heartbeat nor a guaranteed inactivity/
request latency bound. Text deltas show progress, but a quiet period cannot
distinguish slow inference from a stalled request. Short successful runs do not
verify an arbitrary release threshold. The script's 120-second per-invocation
safety bound and ten-second local cleanup bound protect the test runner only:
they are not proposed application timeout defaults. If exceeded, the test
explicitly fails, kills only owned work, and does not retry.

These two gaps are deliberately deferred potential improvements, not failed
checks to relabel as passes and not blockers for completing #4. The user-directed
decision is recorded in Epic #1. A future improvement may establish an
application inactivity policy and safely reproduce genuine service/auth failures,
including session behavior afterward; no provider SLA is required merely to
define an application policy. Do not reset working saved credentials to obtain
that evidence. Fake-CLI tests in #6 may exercise application timeout logic, but
cannot prove real Copilot service behavior. No broader provider, production
adapter, credential reset, or arbitrary application default is introduced here.

## Application discovery and readiness

Task #8 implements connection preparation. The app pins the verified
bundled 1.0.81 via `--no-auto-update`; it rejects other versions rather than
assuming their permission/framing behavior is compatible. No install/update/login
is invoked. A selected `.exe` must be trusted by the user; choosing an arbitrary
executable does not make it a supported provider.

Automatic discovery uses the first application named `copilot.exe` in PATH,
then the documented portable-package roots
`%LOCALAPPDATA%\Microsoft\WinGet\Packages` and
`%PROGRAMFILES%\WinGet\Packages`, restricted to `GitHub.Copilot_*` package
directories containing `copilot.exe`. The
[WinGet settings reference](https://learn.microsoft.com/en-us/windows/package-manager/winget/settings#portablepackageuserroot-setting)
documents these defaults and configurable roots; the
[1.0.81 installer manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/g/GitHub/Copilot/v1.0.81/GitHub.Copilot.installer.yaml)
confirms a portable `copilot.exe`. The actual user-root package was observed
locally on 2026-11-08. No guessed npm, release-download, version-specific, or
custom package directory is searched. Configure those installations explicitly
or expose their actual executable on PATH.

Readiness starts version and inference subprocesses through the existing
`ProcessTransport`. It uses an app-isolated CLI home/working directory, the
same zero-tool restrictions above, and a new UUID. Existing secure CLI
authentication/inherited supported token variables are used without reading,
collecting or saving credentials. No user's prior conversations or CLI
configuration are loaded. A short synthetic connection prompt costs one normal
service request; Auto-connect defaults off. Missing configuration guidance
still appears with Auto-connect off, without a service call.

The check requires successful OS exit, empty stderr, complete valid JSONL,
an assistant response without tool requests/terminal escapes, at least one
model call with zero tools, and a matching successful session result.
Failure is actionable and never retried automatically. Closing cancels/settles
owned work. Readiness output is bounded to 1 MiB per stream; this is a diagnostic
memory bound, not a response-latency SLA. The deliberately deferred production
stall policy remains unchanged: connection preparation is cancellable by Exit,
not assigned an invented provider timeout. A readiness success reflects that
request, not a permanent network connection.

## Production streamed-turn adapter

Task #9 reuses the exact readiness request builder and `ProcessTransport` for
user SEND requests. Incremental JSONL framing handles arbitrary pipe chunks;
only `assistant.message_delta.data.deltaContent` and final-only
`assistant.message.data.content` become transcript text. Terminal CSI, OSC and
control strings are removed with state retained across text deltas; normal
multiline/Unicode text remains intact. Final content is not displayed twice.
Unknown diagnostic events are ignored, not presented as assistant output.

Completion requires matching streamed/final content, a nonempty clean response,
zero-tool model evidence, no tool activity/requests or session error, a matching
successful result, successful OS exit and empty stderr. Malformed/out-of-order
response events fail explicitly. Each event, accumulated raw response and stderr
has a 1 MiB memory safety bound. This is not a stall/watchdog policy.

The UI asynchronously appends deltas on its dispatcher, disables concurrent
SEND/settings/connection operations, preserves failed input and partial output,
and records per-turn timestamps plus completed/failed/cancelled state. Exit
settles the new owned subprocess path. Stop/full recovery, session reset,
keyboard/transcript controls and export remain separately assigned tasks.
No broader CLI compatibility, full-product acceptance or consciously deferred
real failure/timeout guarantees are implied.
