# win95-copilot

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