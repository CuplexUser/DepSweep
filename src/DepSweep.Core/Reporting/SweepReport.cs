using DepSweep.Core.Config;
using DepSweep.Core.Models;
using DepSweep.Core.Sweep;

namespace DepSweep.Core.Reporting;

/// <summary>Projects that share a git repository (or the projects that are not in any repository).</summary>
public sealed record RepoGroup
{
    /// <summary>Repository root, or null for projects outside git.</summary>
    public string? RepoRoot { get; init; }

    public string? Branch { get; init; }

    public required IReadOnlyList<ProjectResult> Projects { get; init; }

    /// <summary>Changed files relative to the repo root (forward slashes); absolute when not in git.</summary>
    public required IReadOnlyList<string> ChangedFiles { get; init; }

    public SeverityCounts Before { get; init; } = SeverityCounts.Empty;

    public SeverityCounts After { get; init; } = SeverityCounts.Empty;

    public SeverityCounts Fixed => Before.Reduction(After);

    public bool HasChanges => ChangedFiles.Count > 0;

    public string? SuggestedCommitMessage { get; init; }

    /// <summary>A ready-to-paste <c>git add</c> command for the changed files.</summary>
    public string? GitAddCommand { get; init; }

    public string DisplayName => RepoRoot is null ? "(not in a git repository)" : Path.GetFileName(RepoRoot);
}

public sealed record SweepReport
{
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public bool Force { get; init; }
    public bool DryRun { get; init; }
    public bool Cancelled { get; init; }
    public required IReadOnlyList<ProjectResult> Projects { get; init; }
    public required IReadOnlyList<RepoGroup> Repositories { get; init; }

    public IEnumerable<RepoGroup> ChangedRepositories => Repositories.Where(r => r.HasChanges);

    public SeverityCounts TotalBefore => Projects.Aggregate(SeverityCounts.Empty, (acc, p) => acc + (p.Before?.Counts ?? SeverityCounts.Empty));

    public SeverityCounts TotalRemaining => Projects.Aggregate(SeverityCounts.Empty, (acc, p) => acc + (p.Current?.Counts ?? SeverityCounts.Empty));

    public static SweepReport Create(
        IReadOnlyList<ProjectResult> results,
        SweepOptions options,
        DateTimeOffset started,
        DateTimeOffset finished,
        bool cancelled = false)
    {
        var groups = results
            .GroupBy(r => r.Git?.RepoRoot is { } root ? PathUtil.Normalize(root) : null, PathUtil.Comparer)
            .Select(g => BuildGroup(g.Key, g.ToList(), options.CommitMessageTemplate))
            .OrderByDescending(g => g.HasChanges)
            .ThenBy(g => g.RepoRoot is null)
            .ThenBy(g => g.RepoRoot, PathUtil.Comparer)
            .ToList();

        return new SweepReport
        {
            StartedAt = started,
            FinishedAt = finished,
            Force = options.Force,
            DryRun = options.DryRun,
            Cancelled = cancelled,
            Projects = results,
            Repositories = groups,
        };
    }

    private static RepoGroup BuildGroup(string? repoRoot, List<ProjectResult> projects, string template)
    {
        var changed = projects
            .SelectMany(p => p.ChangedFiles)
            .Select(f => repoRoot is null ? f : PathUtil.ToRelativeForwardSlash(repoRoot, f))
            .Distinct(PathUtil.Comparer)
            .Order(PathUtil.Comparer)
            .ToList();

        var before = projects.Aggregate(SeverityCounts.Empty, (acc, p) => acc + (p.Before?.Counts ?? SeverityCounts.Empty));
        var after = projects.Aggregate(SeverityCounts.Empty, (acc, p) => acc + (p.Current?.Counts ?? SeverityCounts.Empty));
        var changedProjects = projects.Where(p => p.ChangedFiles.Count > 0).ToList();

        var group = new RepoGroup
        {
            RepoRoot = repoRoot,
            Branch = projects.Select(p => p.Git?.Branch).FirstOrDefault(b => b is not null),
            Projects = projects.OrderBy(p => p.Project.Path, PathUtil.Comparer).ToList(),
            ChangedFiles = changed,
            Before = before,
            After = after,
        };

        if (changed.Count == 0)
        {
            return group;
        }

        var fixedCounts = changedProjects.Aggregate(SeverityCounts.Empty, (acc, p) => acc + p.FixedCounts);
        return group with
        {
            SuggestedCommitMessage = CommitMessage.Build(template, fixedCounts, changedProjects.Select(p => p.Project.Name)),
            GitAddCommand = repoRoot is null ? null : BuildGitAdd(repoRoot, changed),
        };
    }

    private static string BuildGitAdd(string repoRoot, IEnumerable<string> files) =>
        $"git -C \"{repoRoot}\" add -- " + string.Join(' ', files.Select(f => f.Contains(' ') ? $"\"{f}\"" : f));
}

public static class CommitMessage
{
    public static string Build(string template, SeverityCounts fixedCounts, IEnumerable<string> projectNames)
    {
        var names = projectNames.Distinct(StringComparer.Ordinal).ToList();
        var message = template
            .Replace("{fixedCount}", fixedCounts.Total.ToString(), StringComparison.Ordinal)
            .Replace("{fixedSummary}", fixedCounts.ToSummary(), StringComparison.Ordinal)
            .Replace("{projects}", string.Join(", ", names), StringComparison.Ordinal);

        // A template built around fixed counts reads badly when files changed but nothing was fixed.
        return fixedCounts.Total == 0 ? "chore(deps): update lockfile via audit fix" : message;
    }
}
