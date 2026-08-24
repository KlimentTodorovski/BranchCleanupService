# Branch Cleanup Windows Service — Implementation Plan

**Goal:** Build a standalone .NET Windows Service that automates the
`git fetch -p && git branch -vv | ... | xargs git branch -D` habit,
running daily during working hours across one or more configured local
repos.

**Architecture:** A Generic Host (`Microsoft.Extensions.Hosting`) console
app that self-registers as a Windows Service via
`Microsoft.Extensions.Hosting.WindowsServices`. A single
`BackgroundService` (`CleanupWorker`) polls on an interval and, once per
weekday after a configured hour, invokes `BranchCleaner` once per
configured repo path. `BranchCleaner` shells out to `git.exe` (fetch,
list, delete) and applies the "skip current branch" / "skip protected
branch" safety rules. Serilog writes daily rolling log files.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Hosting`,
`Microsoft.Extensions.Hosting.WindowsServices`, `Serilog.AspNetCore`,
`Serilog.Sinks.File`. Plain `PackageReference` with explicit versions —
no central package management.

**Spec:** [`docs/design.md`](./design.md)

## Global Constraints

- Standalone project — not meant to be added to any other repo's
  solution, and has no shared build configuration or central package
  management with any repo it manages.
- `TargetFramework` is `net10.0`, `OutputType` is `Exe`.
- No automated unit tests — every task's verification step is a manual
  build/run check instead.
- `ProtectedBranches` defaults to `["master", "dev"]` and must never be
  deleted regardless of tracking status.
- The currently checked-out branch in a repo must never be deleted, even
  if it reports `gone` tracking — it must be logged as skipped instead.
- Windows Service startup type is `auto` (Automatic) so it starts at
  machine boot without a logged-in user.
- Log files roll daily (`RollingInterval.Day`) with
  `retainedFileCountLimit: 14` (delete-after-14-days, not archiving).

---

### Task 1: Project scaffold, configuration, and minimal host

**Files:**
- `BranchCleanupService.csproj`
- `appsettings.json`
- `CleanupOptions.cs`
- `Program.cs`

**Interfaces:**
- Produces: `CleanupOptions` (public sealed class, namespace
  `BranchCleanupService`) with `public const string SectionName =
  "BranchCleanup"` and properties `string[] RepoPaths`, `string[]
  ProtectedBranches`, `int RunAtHour`, `bool RunOnWeekdaysOnly`, `int
  PollIntervalSeconds`, `string GitExecutablePath`. Bound into DI via
  `builder.Services.Configure<CleanupOptions>(...)`, consumed later as
  `IOptions<CleanupOptions>`.
- Produces: a running `IHost` with Serilog wired to
  `Logs/branch-cleanup-.log` (daily rolling, 14-file retention) and
  `AddWindowsService()` already registered, ready for Task 2 to add the
  worker.

**Steps:**
1. Create `BranchCleanupService.csproj` (`OutputType=Exe`,
   `TargetFramework=net10.0`, package references for
   `Microsoft.Extensions.Hosting`,
   `Microsoft.Extensions.Hosting.WindowsServices`, `Serilog.AspNetCore`,
   `Serilog.Sinks.File`, and `appsettings.json` set to copy to output).
2. Create `appsettings.json` with the six configuration keys under a
   `BranchCleanup` section.
3. Create `CleanupOptions.cs` binding those keys.
4. Create `Program.cs`: build the host, bind `CleanupOptions`, call
   `AddWindowsService()`, wire Serilog to the daily rolling file sink,
   log a startup message, run the host.
5. `dotnet restore && dotnet build` — expect a clean build.
6. `dotnet run`, wait a few seconds, Ctrl+C, confirm
   `Logs/branch-cleanup-<today>.log` contains the startup log line.

---

### Task 2: Branch cleanup logic and worker loop

**Files:**
- `BranchCleaner.cs`
- `CleanupWorker.cs`
- `Program.cs` (register the new services)

**Interfaces:**
- Consumes: `CleanupOptions` from Task 1 (`IOptions<CleanupOptions>`).
- Produces: `BranchCleaner` (public sealed class), constructor
  `(IOptions<CleanupOptions> options, ILogger<BranchCleaner> logger)`,
  method `Task CleanupRepositoryAsync(string repoPath, CancellationToken
  cancellationToken)`.
- Produces: `CleanupWorker : BackgroundService` (public sealed class),
  constructor `(BranchCleaner branchCleaner, IOptions<CleanupOptions>
  options, ILogger<CleanupWorker> logger)`. Registered via
  `AddHostedService<CleanupWorker>()`.

**Steps:**
1. Implement `BranchCleaner.CleanupRepositoryAsync`: verify the `.git`
   directory exists, run `git fetch -p`, run `git branch -vv`, parse
   lines containing `: gone]` into `(name, isCurrent)` pairs, then for
   each candidate either skip (current branch / protected branch) or
   run `git branch -D <name>` and log the outcome. Git commands run via
   `Process` with `ArgumentList` (not a raw argument string) and
   `WorkingDirectory` set to the repo path.
2. Implement `CleanupWorker.ExecuteAsync`: poll every
   `PollIntervalSeconds`; each tick checks weekday, `lastrun.txt`, and
   `RunAtHour` before running a pass across all `RepoPaths` and writing
   `lastrun.txt`. Wrap each tick in a try/catch so one bad tick doesn't
   kill the polling loop.
3. Register `BranchCleaner` and `CleanupWorker` in `Program.cs`.
4. `dotnet build` — expect a clean build.
5. End-to-end test against a real scratch repo + bare "remote":
   create a bare repo, a working repo pointing at it, push two feature
   branches, delete both on the "remote", then check out one of them
   locally so it's both current and gone.
6. Point `appsettings.json` at the scratch repo with `RunAtHour: 0`,
   `RunOnWeekdaysOnly: false`, `PollIntervalSeconds: 5` for a fast
   manual test loop.
7. `dotnet run`, wait ~10s, Ctrl+C. Confirm: the non-checked-out gone
   branch was deleted, the checked-out gone branch was left alone and
   logged as skipped, `lastrun.txt` was written.
8. Revert `appsettings.json` to real defaults and delete the test
   `lastrun.txt`.

---

### Task 3: Install/uninstall scripts and Windows Service verification

**Files:**
- `install-service.ps1`
- `uninstall-service.ps1`
- `appsettings.json` (set the real `RepoPaths`)

**Interfaces:**
- Consumes: the built `BranchCleanupService.exe` (published via `dotnet
  publish`).
- Produces: a registered Windows Service named `BranchCleanupService`
  with `start= auto`.

**Steps:**
1. `install-service.ps1` (`#Requires -RunAsAdministrator`): `dotnet
   publish` to a `publish/` folder, stop/remove any existing service of
   the same name, `sc.exe create ... start= auto`, set a description,
   start the service.
2. `uninstall-service.ps1` (`#Requires -RunAsAdministrator`): stop and
   `sc.exe delete` the service if it exists.
3. Set the real `RepoPaths` in `appsettings.json`.
4. Run `install-service.ps1` from an Administrator PowerShell.
5. Verify with `Get-Service` (Status: Running) and `sc.exe qc` (START_TYPE:
   AUTO_START).
6. Wait a minute and confirm the log file under `publish/Logs/` is being
   written to with no errors.
7. Clean up the scratch test repo/remote created in Task 2.
