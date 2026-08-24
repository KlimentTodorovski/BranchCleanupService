namespace BranchCleanupService;

public sealed class CleanupOptions
{
    public const string SectionName = "BranchCleanup";

    public string[] RepoPaths { get; set; } = [];

    public string[] ProtectedBranches { get; set; } = ["master", "dev"];

    public int RunAtHour { get; set; } = 12;

    public bool RunOnWeekdaysOnly { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 60;

    public string GitExecutablePath { get; set; } = "git";
}
