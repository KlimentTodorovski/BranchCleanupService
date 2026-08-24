namespace BranchCleanupService;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class BranchCleaner(
    IOptions<CleanupOptions> options,
    ILogger<BranchCleaner> logger)
{
    private readonly CleanupOptions _options = options.Value;

    public async Task CleanupRepositoryAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        {
            logger.LogWarning("Skipping {RepoPath}: no .git directory found", repoPath);
            return;
        }

        // "ahead N" (unpushed local commits) can only be read from `branch -vv` before
        // `fetch -p` prunes the stale remote-tracking ref — once pruned, a branch just
        // shows "gone" with no way to tell whether it had commits that were never pushed.
        // This makes the protection best-effort, not a guarantee: ANY prune of that ref —
        // an IDE's background auto-fetch, a manual `git fetch`/`git pull`, another tool —
        // permanently destroys the signal before this method ever runs, with no trace that
        // it happened. If something else already pruned it, this sees a plain "gone" branch
        // and deletes it even if it had unpushed commits. See docs/design.md.
        var preFetchBranchResult = await RunGitAsync(repoPath, ["branch", "-vv"], cancellationToken);
        var aheadCounts = preFetchBranchResult.ExitCode == 0
            ? ParseAheadCounts(preFetchBranchResult.StandardOutput)
            : new Dictionary<string, int>(StringComparer.Ordinal);

        var fetchResult = await RunGitAsync(repoPath, ["fetch", "-p"], cancellationToken);
        if (fetchResult.ExitCode != 0)
        {
            logger.LogError("git fetch -p failed in {RepoPath}: {Error}", repoPath, fetchResult.StandardError);
            return;
        }

        var branchResult = await RunGitAsync(repoPath, ["branch", "-vv"], cancellationToken);
        if (branchResult.ExitCode != 0)
        {
            logger.LogError("git branch -vv failed in {RepoPath}: {Error}", repoPath, branchResult.StandardError);
            return;
        }

        foreach (var (name, isCurrent) in ParseGoneBranches(branchResult.StandardOutput))
        {
            var aheadCount = aheadCounts.GetValueOrDefault(name);
            await DeleteBranchAsync(repoPath, name, isCurrent, aheadCount, cancellationToken);
        }
    }

    private static Dictionary<string, int> ParseAheadCounts(string branchVvOutput)
    {
        var results = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var rawLine in branchVvOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var withoutMarker = line.StartsWith("* ", StringComparison.Ordinal) ? line[2..] : line.TrimStart();
            var branchNameEnd = withoutMarker.IndexOf(' ');
            if (branchNameEnd <= 0)
            {
                continue;
            }

            var branchName = withoutMarker[..branchNameEnd];

            var trackingStart = withoutMarker.IndexOf('[');
            var trackingEnd = withoutMarker.IndexOf(']');
            if (trackingStart < 0 || trackingEnd < trackingStart)
            {
                continue;
            }

            var tracking = withoutMarker[(trackingStart + 1)..trackingEnd];
            var aheadMarker = tracking.IndexOf("ahead ", StringComparison.Ordinal);
            if (aheadMarker < 0)
            {
                continue;
            }

            var numberStart = aheadMarker + "ahead ".Length;
            var numberEnd = numberStart;
            while (numberEnd < tracking.Length && char.IsAsciiDigit(tracking[numberEnd]))
            {
                numberEnd++;
            }

            if (numberEnd > numberStart && int.TryParse(tracking[numberStart..numberEnd], out var aheadCount))
            {
                results[branchName] = aheadCount;
            }
        }

        return results;
    }

    private static List<(string Name, bool IsCurrent)> ParseGoneBranches(string branchVvOutput)
    {
        var results = new List<(string Name, bool IsCurrent)>();

        foreach (var rawLine in branchVvOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var isCurrent = line.StartsWith("* ", StringComparison.Ordinal);
            var withoutMarker = isCurrent ? line[2..] : line.TrimStart();

            if (!withoutMarker.Contains(": gone]", StringComparison.Ordinal))
            {
                continue;
            }

            var branchName = withoutMarker.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            results.Add((branchName, isCurrent));
        }

        return results;
    }

    private async Task DeleteBranchAsync(string repoPath, string branchName, bool isCurrent, int aheadCount, CancellationToken cancellationToken)
    {
        if (isCurrent)
        {
            logger.LogInformation("Skipped (checked out): {Branch} in {RepoPath}", branchName, repoPath);
            return;
        }

        if (_options.ProtectedBranches.Contains(branchName, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogInformation("Skipped (protected): {Branch} in {RepoPath}", branchName, repoPath);
            return;
        }

        if (aheadCount > 0)
        {
            logger.LogInformation(
                "Skipped (unpushed local commits): {Branch} in {RepoPath} is {AheadCount} commit(s) ahead of its last known remote state",
                branchName, repoPath, aheadCount);
            return;
        }

        var deleteResult = await RunGitAsync(repoPath, ["branch", "-D", branchName], cancellationToken);
        if (deleteResult.ExitCode == 0)
        {
            logger.LogInformation("Deleted: {Branch} in {RepoPath}", branchName, repoPath);
        }
        else
        {
            logger.LogWarning("Failed to delete {Branch} in {RepoPath}: {Error}", branchName, repoPath, deleteResult.StandardError);
        }
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_options.GitExecutablePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }
}
