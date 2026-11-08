# Epic worker kickoff

Use the repository custom agent **💾 95 Developer** defined in
`.github/agents/epic-developer.agent.md`.

Complete and merge at most one eligible direct native child task of Epic:
https://github.com/iricigor/win95-copilot/issues/1

Repository: `iricigor/win95-copilot`. Target branch: `main`.

This is the user-approved standalone workflow, not a Dispatcher assignment.
Within the approved task scope, independently select, claim, implement,
validate, repair, and merge only verified work without bypassing checks or
reviews. Report on the task and Epic, then stop. If unfinished work or a retained
claim exists anywhere in the repository, stop without taking another task.
If there are no eligible native child tasks, report that and make no changes.
Do not create the missing task breakdown or implement the Epic directly.

Follow the agent definition and repository instructions. Verification is
automation-first: automate feasible checks; for unavailable real-service/isolated
desktop access or checks that cannot reasonably be automated, make bounded
best-effort checks and save/read back a non-blocking note on the Epic. Do not
require human testing/sign-off or retain a claim solely for those limitations.
Actual failures, unsafe behavior, missing implementation inputs/instructions,
permissions, uncertain ownership and applicable required CI/reviews remain
blockers. Never fabricate evidence, bypass gates or silently substitute another
agent or broader workflow.
