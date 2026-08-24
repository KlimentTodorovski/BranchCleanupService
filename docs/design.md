# Branch Cleanup Windows Service — Design

## Problem

A common developer habit is running this manually (e.g. every Monday) to
prune local branches whose remote tracking branch was deleted:

```bash
git fetch -p && git branch -vv | awk '/: gone]/{print $1}' | xargs git branch -D
```

This should run automatically, daily, during working hours, without
requiring a terminal session to be open. It must never fail loudly or
destructively when the branch being deleted happens to be the one the
user currently has checked out, and it must never delete protected
branches (e.g. `master`/`dev`) regardless of tracking status.

## Scope

A standalone .NET console app, hosted as a Windows Service, that
periodically prunes gone-tracking local branches across one or more
configured git repository paths. It has no dependency on, or
relationship to, any of the repos it's configured to clean up — no
shared build configuration, central package management, or solution
membership with them.

Out of scope: remote branch deletion, non-Windows hosts, any UI.

## Project Shape

- `BranchCleanupService.csproj` — `OutputType=Exe`,
  `TargetFramework=net10.0`, plain `<PackageReference>` entries with
  explicit `Version` attributes (no central package management).
- `Program.cs` — builds a Generic Host, wires Serilog, calls
  `AddWindowsService()`, registers `CleanupWorker` as a `BackgroundService`.
- `CleanupWorker.cs` — polling loop and run-gating logic.
- `BranchCleaner.cs` — the actual git plumbing (fetch, parse, delete) for
  a single repo path.
- `appsettings.json` — configuration (see below).
- `install-service.ps1`, `uninstall-service.ps1` — helper scripts to
  register/unregister the compiled exe as a Windows Service via `sc.exe`.

### Packages

- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Hosting.WindowsServices`
- `Serilog.AspNetCore`
- `Serilog.Sinks.File`

## Configuration

`appsettings.json`:

| Key | Type | Default | Meaning |
|---|---|---|---|
| `RepoPaths` | `string[]` | `[]` (must be set) | Local git repo paths to clean up |
| `ProtectedBranches` | `string[]` | `["master", "dev"]` | Branch names never deleted, even if reported gone |
| `RunAtHour` | `int` | `12` | Local hour (24h) to trigger the daily run |
| `RunOnWeekdaysOnly` | `bool` | `true` | Skip Saturday/Sunday |
| `PollIntervalSeconds` | `int` | `60` | How often the worker checks whether it's time to run |
| `GitExecutablePath` | `string` | `"git"` | Path to the git executable; must be resolvable (PATH or absolute) |

## Runtime Behavior

### Scheduling

`CleanupWorker` is a `BackgroundService` with a simple poll loop (interval
`PollIntervalSeconds`). On each tick:

1. If `RunOnWeekdaysOnly` is true and today is Saturday/Sunday, do nothing.
2. Read `lastrun.txt` (a single ISO date, stored next to the executable).
   If it already equals today's date, do nothing — already ran today.
3. If the current local time's hour >= `RunAtHour`, run a cleanup pass
   across all `RepoPaths`, then write today's date to `lastrun.txt`.

Step 3's `>=` (not `==`) means: if the service was offline at `RunAtHour`
(machine asleep, service not yet installed/started that day) and comes up
later in the day, it catches up immediately instead of waiting for
tomorrow. The `lastrun.txt` date check prevents double-running if the
service restarts later the same day.

### Cleanup pass, per repo path

1. Verify the path exists and contains a `.git` directory; if not, log a
   warning and skip that repo (don't crash the whole pass over one bad
   entry).
2. Before fetching, run `git branch -vv` once and record any `ahead N`
   count per branch. This has to happen *before* `fetch -p`: once the
   prune removes a stale remote-tracking ref, git can no longer tell you
   whether the branch had commits that were never pushed — the ahead/
   behind comparison target is simply gone. This snapshot is the only
   chance to catch it.
3. Run `git fetch -p` in that repo (via `Process`, `WorkingDirectory` set
   to the repo path).
4. Run `git branch -vv` again. Parse each line:
   - Strip a leading `* ` (marks the currently checked-out branch) — if
     present, remember this branch as "current" for this repo.
   - A line is a deletion candidate only if it contains `: gone]`.
   - The branch name is the first whitespace-separated token after
     stripping the `* ` marker.
5. For each deletion candidate:
   - If it's the currently checked-out branch → log "skipped (checked
     out)", do not attempt deletion. (This is the exact failure mode the
     original bash one-liner would hit — `xargs git branch -D` erroring
     out on the checked-out branch. Handled explicitly instead of relying
     on git's own error.)
   - If its name is in `ProtectedBranches` → log "skipped (protected)",
     do not attempt deletion.
   - If the pre-fetch snapshot showed it `ahead N` (N > 0) of its last
     known remote state → log "skipped (unpushed local commits)" with
     the count, do not attempt deletion. This is a best-effort check: it
     only catches unpushed commits on the *first* run after the remote
     branch disappears. Once a run has fetched-and-pruned once, the
     comparison target is gone for good and later runs can no longer
     detect it — there's no local history of "what the ahead count used
     to be" beyond that first observation.
   - Otherwise run `git branch -D <name>`. Log success or failure
     (capturing stderr) per branch. A single branch failing to delete
     (e.g. it's checked out in a linked worktree) does not stop the rest
     of the batch.

### Logging

Serilog file sink with `RollingInterval.Day`, written to
`Logs/branch-cleanup-.log` next to the executable. A new file is created
each day with the date embedded in the filename (e.g.
`branch-cleanup-20260824.log`). `retainedFileCountLimit: 14` makes
Serilog automatically delete the oldest file once more than 14 daily
files exist — this is deletion, not compression/archiving; nothing is
zipped or moved elsewhere. Each run logs: start/end, per-repo branch
fetch result, and per-branch action (deleted / skipped + reason / failed
+ reason).

## Error Handling

- A misconfigured or unreachable repo path: logged and skipped, other
  repos still process.
- `git` not found on PATH / `GitExecutablePath` invalid: logged as an
  error for that run; the service keeps running and retries on the next
  scheduled day (it does not crash the host process).
- Any unhandled exception inside a single cleanup pass is caught at the
  top of `CleanupWorker`'s tick handler, logged, and does not take down
  the polling loop.

## Testing

No automated unit tests — this is a thin console utility over
`Process`/git CLI calls. Verification is manual: run the built exe
directly (not installed as a service) against a scratch repo with a
couple of gone-tracking branches and confirm log output and branch state
match expectations.

## Install / Uninstall

`install-service.ps1` (run as Administrator):
- Publishes the project and locates the built exe.
- `sc.exe create BranchCleanupService binPath= "<path>\BranchCleanupService.exe" start= auto`
  — `start= auto` registers it as an Automatic-start service, so on a
  local dev machine Windows launches it at boot (before/without any user
  login), and it stays running in the background afterward. No separate
  "run on login" mechanism is needed.
- Starts the service immediately so it's active right after install, not
  only after the next reboot.

`uninstall-service.ps1`:
- Stops and deletes the service via `sc.exe`.

Not part of the build — these are one-time setup scripts you run
manually after building/publishing.
