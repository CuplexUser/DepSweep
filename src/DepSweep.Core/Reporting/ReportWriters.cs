using System.Text;
using System.Text.Json;
using DepSweep.Core.Config;
using DepSweep.Core.Models;

namespace DepSweep.Core.Reporting;

public static class MarkdownReportWriter
{
    public static string Write(SweepReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DepSweep report");
        sb.AppendLine();
        var mode = report.DryRun ? "dry run" : report.Force ? "fix (--force)" : "fix";
        sb.AppendLine($"- **Run:** {report.StartedAt:yyyy-MM-dd HH:mm} ({mode}, {(report.FinishedAt - report.StartedAt).TotalSeconds:0}s){(report.Cancelled ? " — **cancelled**" : "")}");
        sb.AppendLine($"- **Projects:** {report.Projects.Count} — vulnerabilities before: {report.TotalBefore.ToSummary()}; remaining: {report.TotalRemaining.ToSummary()}");
        var changed = report.ChangedRepositories.ToList();
        sb.AppendLine($"- **Repositories with changes to commit:** {changed.Count}");
        sb.AppendLine();

        if (changed.Count > 0)
        {
            sb.AppendLine("## Repositories to commit");
            sb.AppendLine();
            foreach (var repo in changed)
            {
                WriteRepo(sb, repo);
            }
        }

        var unchanged = report.Repositories.Where(r => !r.HasChanges).ToList();
        if (unchanged.Count > 0)
        {
            sb.AppendLine("## No changes");
            sb.AppendLine();
            foreach (var repo in unchanged)
            {
                WriteRepo(sb, repo);
            }
        }

        return sb.ToString();
    }

    private static void WriteRepo(StringBuilder sb, RepoGroup repo)
    {
        sb.AppendLine(repo.RepoRoot is null
            ? $"### {repo.DisplayName}"
            : $"### {repo.DisplayName} (`{repo.Branch}`)");
        if (repo.RepoRoot is not null)
        {
            sb.AppendLine($"`{repo.RepoRoot}`");
        }

        sb.AppendLine();
        sb.AppendLine("| Project | Manager | Status | Before (C/H/M/L) | After (C/H/M/L) | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var p in repo.Projects)
        {
            var rel = repo.RepoRoot is null ? p.Project.Path : PathUtil.ToRelativeForwardSlash(repo.RepoRoot, p.Project.Path);
            sb.AppendLine($"| {Escape(p.Project.Name)} (`{rel}`) | {p.Project.PackageManager.ToString().ToLowerInvariant()} | {p.Status} | {p.Before?.Counts.ToCompact() ?? "-"} | {p.After?.Counts.ToCompact() ?? "-"} | {Escape(p.Message ?? "")} |");
        }

        sb.AppendLine();
        if (repo.HasChanges)
        {
            sb.AppendLine("**Changed files:**");
            foreach (var f in repo.ChangedFiles)
            {
                sb.AppendLine($"- `{f}`");
            }

            sb.AppendLine();
            if (repo.GitAddCommand is not null)
            {
                sb.AppendLine("```");
                sb.AppendLine(repo.GitAddCommand);
                sb.AppendLine($"git -C \"{repo.RepoRoot}\" commit -m \"{repo.SuggestedCommitMessage}\"");
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        var remaining = repo.Projects
            .SelectMany(p => (p.Current?.Vulnerabilities ?? []).Select(v => (p.Project, v)))
            .ToList();
        if (remaining.Count > 0 && repo.Projects.Any(p => p.After is not null))
        {
            sb.AppendLine("<details><summary>Remaining vulnerabilities</summary>");
            sb.AppendLine();
            foreach (var (project, v) in remaining)
            {
                sb.AppendLine($"- **{v.Package}** ({v.Severity.ToDisplay()}{(v.IsDirect ? ", direct" : "")}) in {project.Name} — {v.FixDescription}");
            }

            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}

public static class JsonReportWriter
{
    public static string Write(SweepReport report) => JsonSerializer.Serialize(ToDto(report), ConfigStore.JsonOptions);

    public static string WriteScan(IEnumerable<ProjectResult> results) =>
        JsonSerializer.Serialize(results.Select(ProjectDto).ToList(), ConfigStore.JsonOptions);

    private static object ToDto(SweepReport r) => new
    {
        r.StartedAt,
        r.FinishedAt,
        r.Force,
        r.DryRun,
        r.Cancelled,
        totalBefore = r.TotalBefore,
        totalRemaining = r.TotalRemaining,
        repositories = r.Repositories.Select(g => new
        {
            g.RepoRoot,
            g.Branch,
            g.HasChanges,
            g.ChangedFiles,
            g.SuggestedCommitMessage,
            g.GitAddCommand,
            before = g.Before,
            after = g.After,
            projects = g.Projects.Select(ProjectDto),
        }),
    };

    private static object ProjectDto(ProjectResult p) => new
    {
        name = p.Project.Name,
        path = p.Project.Path,
        packageManager = p.Project.PackageManager,
        status = p.Status,
        message = p.Message,
        repoRoot = p.Git?.RepoRoot,
        branch = p.Git?.Branch,
        before = p.Before?.Counts,
        after = p.After?.Counts,
        changedFiles = p.ChangedFiles,
        vulnerabilities = (p.Current?.Vulnerabilities ?? []).Select(v => new
        {
            v.Package,
            v.Severity,
            v.IsDirect,
            v.Fix,
            v.FixVersion,
            advisories = v.Advisories,
        }),
    };
}

/// <summary>Saves each fix run's report under %LOCALAPPDATA%\DepSweep\reports.</summary>
public sealed class ReportStore(string? directory = null)
{
    public string Directory { get; } = directory
        ?? Environment.GetEnvironmentVariable("DEPSWEEP_REPORT_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepSweep", "reports");

    /// <summary>Writes Markdown and JSON copies and returns the Markdown path.</summary>
    public string Save(SweepReport report)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var stem = Path.Combine(Directory, report.StartedAt.ToString("yyyyMMdd-HHmmss"));
        File.WriteAllText(stem + ".json", JsonReportWriter.Write(report));
        File.WriteAllText(stem + ".md", MarkdownReportWriter.Write(report));
        return stem + ".md";
    }

    /// <summary>Path of the newest saved report with the given extension (".md" or ".json"), or null.</summary>
    public string? Latest(string extension = ".md") =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*" + extension).OrderDescending(StringComparer.Ordinal).FirstOrDefault()
            : null;
}
