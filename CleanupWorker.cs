namespace BranchCleanupService;

using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class CleanupWorker(
    BranchCleaner branchCleaner,
    IOptions<CleanupOptions> options,
    ILogger<CleanupWorker> logger) : BackgroundService
{
    private readonly CleanupOptions _options = options.Value;
    private readonly string _lastRunFilePath = Path.Combine(AppContext.BaseDirectory, "lastrun.txt");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unhandled error during cleanup tick");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.Now;

        if (_options.RunOnWeekdaysOnly &&
            (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday))
        {
            return;
        }

        var today = DateOnly.FromDateTime(now);
        if (ReadLastRunDate() == today)
        {
            return;
        }

        if (now.Hour < _options.RunAtHour)
        {
            return;
        }

        logger.LogInformation("Starting branch cleanup run for {RepoCount} repo(s)", _options.RepoPaths.Length);

        foreach (var repoPath in _options.RepoPaths)
        {
            await branchCleaner.CleanupRepositoryAsync(repoPath, cancellationToken);
        }

        WriteLastRunDate(today);
        logger.LogInformation("Branch cleanup run complete");
    }

    private DateOnly? ReadLastRunDate()
    {
        if (!File.Exists(_lastRunFilePath))
        {
            return null;
        }

        var text = File.ReadAllText(_lastRunFilePath).Trim();
        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private void WriteLastRunDate(DateOnly date)
    {
        File.WriteAllText(_lastRunFilePath, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
