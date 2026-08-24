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
            await DeleteBranchAsync(repoPath, name, isCurrent, cancellationToken);
        }
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

    private async Task DeleteBranchAsync(string repoPath, string branchName, bool isCurrent, CancellationToken cancellationToken)
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
