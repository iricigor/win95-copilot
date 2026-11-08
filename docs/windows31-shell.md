# Windows 3.1 shell reference and local checklist

## Reference and adaptation

The visual reference is **Windows 3.1's default color scheme**, not Windows 95:
[GUIdebook's original Color dialog](https://guidebookgallery.org/pics/gui/settings/appearance/win31-1-1.png)
and [ToastyTech's original window/system-menu screenshot](https://toastytech.com/guis/win31windowing.png).
The [accompanying windowing description](https://toastytech.com/guis/win31.html)
documents title-bar double-click, system-box double-click and border resizing.
These are linked historical references; their images/icons are not bundled or
redistributed. The interface uses original geometric drawing primitives rather
than copied artwork.

The Color dialog explicitly shows dark-blue/white active titles and
**white/black inactive titles**. Windowing shows a centered title, a small dash
in the left system box, down/up triangle minimize/maximize controls, a white
menu strip, black outlines and gray scroll bars. It is not the later underscore,
square and X control set. Restore uses paired up/down triangles. The system
menu offers Restore, Move, Size, Minimize, Maximize and Close (Alt+F4); obsolete
Windows 3.1 task switching is left to the host OS, not imitated.

The adaptation uses #C0C0C0 panels, #000080 active titles, white/black outlines,
#808080 bevel shadows, a 21-DIP title strip, 20-DIP title controls, 16-DIP
scroll bars, a five-DIP square outer sizing frame, and compact six/eight-DIP
content spacing. Microsoft Sans Serif at 12 DIP is the available scalable
successor to the reference's raster MS Sans Serif; Courier New at 14 DIP is the
available period-appropriate monospaced text font. This is modern WPF with
normal OS scaling, not pixel-identical emulation of a 640x480 raster desktop.
There is no custom scaling/accessibility framework.

Shared WPF window/control templates provide square bevels and solid fills.
Native caption buttons, glass and rounded chrome are disabled; DWM nonclient
rendering and Windows 11 corner rounding are disabled for created windows.
Windows 10 lacks the optional Windows 11 corner attribute; only its documented
unsupported-attribute result is tolerated. No gradients, opacity/translucency,
drop shadows, copied icons, or dedicated close button are used.

Main and About share the same chrome/system-menu/close path. The main window
retains native caption dragging, title double-click maximize/restore and border
resizing through WPF WindowChrome. The dialog has no minimize/maximize controls.
The body keeps the requested utility area, transcript, multiline input,
SEND/Stop surfaces and status bar.

## Implemented versus future actions

File Exit, system-menu actions, normal Alt+F4, and Help/About work. About is
owned/modal, displays generated application metadata, and closes with OK,
Enter, Escape or its system menu. Help in the utility area shares About's
command. Backend Settings and Connect now share commands between the utility
area and Options. The owned settings dialog uses the same retro resources,
supports Browse/OK/Cancel, preserves preferences and shows validation errors.
Auto-connect is persisted and checked truthfully; connection checks are asynchronous.

Save Transcript (#13), Clear Window (#12), Edit and send/wrap/timestamp options
(#11), Send (#9) and Stop (#10) are
visibly disabled through shared commands. There are no clickable no-op stubs or
fake connected/ready status. The input is editable and supports ordinary text
editing; this does not yet enable submission or implement either send shortcut.
The transcript is read-only. Only Auto-connect is enabled as a preference;
other controls remain disabled until their behavior is implemented.

## Automation-first verification and optional local review

Run the README restore/build/test commands and automate feasible command,
layout, window-state, dialog and interaction checks using synthetic text and
isolated settings. Build and launch a run-owned local app instance. Prefer an
isolated desktop where functional; local interactive testing is authorized for
verified test-app process IDs/window handles when input cannot work in isolation.
Use Windows UI Automation/Win32 controls where available, recheck ownership
before input, and never interact with unrelated windows or real user settings.
Close owned windows/helpers after testing. Use the steps below as coverage guidance and optional human review,
not a mandatory manual checklist or sign-off.

Do not classify menus, keyboard or dialogs as unavailable merely because an
initial non-input desktop probe failed; attempt owned-window local testing first.
If actual desktop appearance or an interaction cannot reasonably be verified
automatically, make bounded best-effort checks and save/read back a non-blocking
note on Epic #1 linking the task/PR/revision, attempts and remaining checks.
Resource/action-state tests and Windows CI are **not visual acceptance**; never
label unavailable evidence as passing. Record OS/scaling/build revision and
reference links where available. Actual defects and failed applicable tests
remain blockers; missing manual review alone does not block delivery.

1. Compare the active main title with the two linked references: centered white
   title on solid navy, dash system box, down/up triangles, square gray/black
   borders, white menu strip, crisp bevels, system and monospaced text fonts.
   Confirm no X, gradient, rounding, translucency or modern shadow.
2. Open Help > About and compare the now-inactive main title: white background
   with black text. Confirm the dialog's navy active title, actual build version,
   square geometry, one OK button and absent sizing controls. Check owner
   modality, Enter, Escape and OK independently.
3. Open each File/Edit/Options/Help menu with the mouse, then with Alt+F/E/O/H,
   arrow keys and Escape. Inspect square, white/black popup borders and navy
   selection. Check mnemonic actions for Exit/About. All future actions must be
   visibly gray and must not execute through mouse or keyboard.
4. Click the system box, then use Alt+Space: Restore/Move/Size/Minimize/Maximize/
   Close must have truthful state. Exercise keyboard Move/Size, caption dragging,
   minimize/restore, maximize/restore, title double-click, and edge/corner resizing.
   Minimized-window/task switching uses modern OS behavior, not a fake desktop.
5. Resize to 520x340 DIP minimum and a larger size: input, SEND, Stop and status
   remain usable and visible. Enter multiline synthetic text into the input;
   confirm transcript text cannot be edited/pasted into. Populate enough
   synthetic transcript text in a test fixture to inspect vertical/horizontal
   scroll arrows/thumbs. Do not count an empty pane as scroll-bar verification.
6. Relaunch independently to check File Exit, system-menu Close, system-box
   double-click and Alt+F4. Each must exit normally, settling owned readiness
   work if a connection check is in progress.

Record actual failures as blockers rather than checking off appearance from XAML.
Record unavailable visual/interaction evidence as non-blocking Epic limitations.
Later tasks own generation cleanup, actual Edit menu behavior, preference
streaming and the assembled product checklist.
