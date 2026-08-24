# BranchCleanupService

> **Note:** This is a fully vibe-coded project — designed and
> implemented end-to-end with [Claude Code](https://claude.com/claude-code),
> with no hand-written code.

A Windows Service that automates the classic

```bash
git fetch -p && git branch -vv | awk '/: gone]/{print $1}' | xargs git branch -D
```

habit — pruning local branches whose remote tracking branch has been
deleted (e.g. after a PR merges) — so you don't have to remember to run
it yourself.

Runs daily during working hours across one or more configured local git
repositories. Never touches the branch you currently have checked out,
never deletes protected branches (`master`/`dev` by default) even if
they somehow report as gone, and never deletes a gone branch that still
has commits you never pushed anywhere.

## How it works

Once installed as a Windows Service, it polls in the background and,
once per weekday after a configured hour (default: 12:00), runs a
cleanup pass over every repo in `RepoPaths`:

1. `git branch -vv` (before fetching, to catch any unpushed commits
   while there's still something to compare against)
2. `git fetch -p`
3. `git branch -vv` again, looking for branches marked `: gone]`
4. For each gone branch:
   - Currently checked out? → skipped and logged, never deleted.
   - In `ProtectedBranches`? → skipped and logged, never deleted.
   - Had unpushed commits in step 1's snapshot? → skipped and logged,
     never deleted. (This only catches unpushed work on the *first* run
     after the remote branch disappears — once fetched-and-pruned once,
     there's no longer anything to compare against.)
   - Otherwise → `git branch -D <name>`, success/failure logged.

If the machine was asleep or the service wasn't running yet when the
scheduled hour passed, it catches up as soon as it starts instead of
waiting until the next day. It won't run twice in the same day.

See [`docs/design.md`](docs/design.md) for the full design and
[`docs/implementation-plan.md`](docs/implementation-plan.md) for how it
was built.

## Requirements

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (to build/publish)
- `git` available on `PATH` (or point `GitExecutablePath` at it)

## Configuration

Edit `appsettings.json` before installing:

```json
{
  "BranchCleanup": {
    "RepoPaths": ["C:\\path\\to\\your-repo"],
    "ProtectedBranches": ["master", "dev"],
    "RunAtHour": 12,
    "RunOnWeekdaysOnly": true,
    "PollIntervalSeconds": 60,
    "GitExecutablePath": "git"
  }
}
```

| Key | Type | Default | Meaning |
|---|---|---|---|
| `RepoPaths` | `string[]` | `[]` | Local git repo paths to clean up — **must be set** |
| `ProtectedBranches` | `string[]` | `["master", "dev"]` | Branch names never deleted, even if reported gone |
| `RunAtHour` | `int` | `12` | Local hour (24h) to trigger the daily run |
| `RunOnWeekdaysOnly` | `bool` | `true` | Skip Saturday/Sunday |
| `PollIntervalSeconds` | `int` | `60` | How often the service checks whether it's time to run |
| `GitExecutablePath` | `string` | `"git"` | Path to the git executable |

## Install

From an **Administrator** PowerShell:

```powershell
cd BranchCleanupService
.\install-service.ps1
```

This publishes the app to `.\publish\`, registers it as a Windows
Service named `BranchCleanupService` with automatic startup (so it
starts at boot, no login required), and starts it immediately. Logs
land in `.\publish\Logs\`, rolling daily and auto-deleting after 14
days.

## Uninstall

```powershell
.\uninstall-service.ps1
```

## Running manually (no service install)

Useful for testing changes to `appsettings.json` or the code itself:

```powershell
dotnet run
```

No console output is expected — logging is file-only. Check
`Logs\branch-cleanup-<date>.log`.
