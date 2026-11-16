namespace DepSweep.Core.Config;

public sealed class RootFolder
{
    public string Path { get; set; } = "";

    /// <summary>Search sub-folders for projects. When false only the folder itself is considered.</summary>
    public bool Recursive { get; set; } = true;

    public bool Enabled { get; set; } = true;
}

public sealed class DepSweepConfig
{
    public static readonly string[] DefaultExcludedDirectories =
        ["node_modules", "bin", "obj", "dist", "build", "out", "coverage", "vendor", "bower_components"];

    public List<RootFolder> Roots { get; set; } = [];

    /// <summary>Project folders the user has deselected.</summary>
    public List<string> ExcludedProjects { get; set; } = [];

    /// <summary>Directory names never descended into (folders starting with '.' are always skipped).</summary>
    public List<string> ExcludedDirectoryNames { get; set; } = [.. DefaultExcludedDirectories];

    public int Concurrency { get; set; } = 4;

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Suggested commit message template; null uses <see cref="DefaultCommitMessageTemplate"/>.
    /// Placeholders: {fixedCount}, {fixedSummary}, {projects}.
    /// </summary>
    public string? CommitMessageTemplate { get; set; }

    public const string DefaultCommitMessageTemplate =
        "chore(deps): fix audit vulnerabilities ({fixedSummary})";

    public bool IsExcluded(string projectPath) =>
        ExcludedProjects.Any(p => PathUtil.Equal(p, projectPath));

    public void SetExcluded(string projectPath, bool excluded)
    {
        ExcludedProjects.RemoveAll(p => PathUtil.Equal(p, projectPath));
        if (excluded)
        {
            ExcludedProjects.Add(PathUtil.Normalize(projectPath));
        }
    }

    public RootFolder? FindRoot(string path) => Roots.FirstOrDefault(r => PathUtil.Equal(r.Path, path));
}
