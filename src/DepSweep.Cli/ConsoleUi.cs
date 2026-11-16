using DepSweep.Core.Config;
using DepSweep.Core.Models;
using DepSweep.Core.Reporting;
using DepSweep.Core.Sweep;

namespace DepSweep.Cli;

internal static class ConsoleUi
{
    private static readonly object Gate = new();

    public static (IReadOnlyList<NodeProject> Projects, IReadOnlyList<IgnoredFolder> Ignored) Discover(
        DepSweepConfig config, string[]? paths, bool includeExcluded = false)
    {
        var explicitPaths = paths is { Length: > 0 };
        var roots = explicitPaths
            ? paths!.Select(p => new RootFolder { Path = p, Recursive = true })
            : config.Roots;

        var result = SweepService.Discover(config, roots);

        // Exclusions apply to runs over the configured roots; explicit paths always run.
        var projects = includeExcluded || explicitPaths
            ? result.Projects
            : result.Projects.Where(p => !config.IsExcluded(p.Path)).ToList();
        return (projects, result.Ignored);
    }

    public static SweepOptions Options(DepSweepConfig config, int? concurrency)
    {
        var options = SweepOptions.FromConfig(config);
        return concurrency is > 0 ? options with { Concurrency = concurrency.Value } : options;
    }

    public static int SetExcluded(ConfigStore store, string path, bool excluded)
    {
        var config = store.Load();
        config.SetExcluded(path, excluded);
        store.Save(config);
        Console.WriteLine($"{(excluded ? "Excluded" : "Included")} {PathUtil.Normalize(path)}");
        return 0;
    }

    /// <summary>2 when any project errored, 1 when vulnerabilities at or above the threshold remain, else 0.</summary>
    public static int ExitCode(IEnumerable<ProjectResult> results, Severity threshold)
    {
        var list = results.ToList();
        if (list.Any(r => r.Status == ProjectStatus.Error))
        {
            return 2;
        }

        return list.Any(r => (r.Current?.Counts.AtOrAbove(threshold) ?? 0) > 0) ? 1 : 0;
    }

    public static void PrintScanTable(IReadOnlyList<ProjectResult> results)
    {
        Console.WriteLine();
        Console.WriteLine($"{"STATUS",-11} {"CRIT",4} {"HIGH",4} {"MOD",4} {"LOW",4}  {"PM",-4}  {"PROJECT",-28} PATH");
        foreach (var r in results.OrderByDescending(r => r.Current?.Counts.Critical ?? -1).ThenByDescending(r => r.Current?.Counts.High ?? -1).ThenBy(r => r.Project.Path, PathUtil.Comparer))
        {
            var c = r.Current?.Counts;
            var line = $"{r.Status,-11} {Num(c?.Critical),4} {Num(c?.High),4} {Num(c?.Moderate),4} {Num(c?.Low),4}  {r.Project.PackageManager.ToString().ToLowerInvariant(),-4}  {Truncate(r.Project.Name, 28),-28} {r.Project.Path}";
            WriteColored(line, StatusColor(r.Status));
            if (r.Status == ProjectStatus.Error && r.Message is not null)
            {
                WriteColored($"            {r.Message}", ConsoleColor.DarkRed);
            }
        }

        var total = results.Aggregate(SeverityCounts.Empty, (acc, r) => acc + (r.Current?.Counts ?? SeverityCounts.Empty));
        var vulnerable = results.Count(r => r.Status == ProjectStatus.Vulnerable);
        Console.WriteLine();
        Console.WriteLine($"{results.Count} project(s) scanned, {vulnerable} vulnerable, {results.Count(r => r.Status == ProjectStatus.Error)} error(s). Total: {total.ToSummary()}.");
        if (vulnerable > 0)
        {
            Console.WriteLine("Run 'depsweep fix' to apply safe fixes (add --force for breaking upgrades).");
        }
    }

    public static void PrintReport(SweepReport report)
    {
        Console.WriteLine();
        var title = report.DryRun ? "DRY RUN — no files were changed" : "DepSweep report";
        WriteColored($"=== {title} ===", ConsoleColor.Cyan);
        Console.WriteLine($"Projects: {report.Projects.Count}   before: {report.TotalBefore.ToSummary()}   remaining: {report.TotalRemaining.ToSummary()}");
        if (report.Cancelled)
        {
            WriteColored("Run was cancelled; results are partial.", ConsoleColor.Yellow);
        }

        foreach (var repo in report.Repositories)
        {
            Console.WriteLine();
            var header = repo.RepoRoot is null ? repo.DisplayName : $"{repo.DisplayName} [{repo.Branch}]  {repo.RepoRoot}";
            WriteColored(header, repo.HasChanges ? ConsoleColor.White : ConsoleColor.Gray);

            foreach (var p in repo.Projects)
            {
                var counts = p.After is not null
                    ? $"{p.Before!.Counts.ToCompact()} -> {p.After.Counts.ToCompact()}"
                    : p.Before?.Counts.ToCompact() ?? "-";
                WriteColored($"  {p.Status,-10} {Truncate(p.Project.Name, 28),-28} {counts,-19} {p.Message}", StatusColor(p.Status));
            }

            if (repo.HasChanges)
            {
                Console.WriteLine("  Changed files:");
                foreach (var f in repo.ChangedFiles)
                {
                    WriteColored($"    {f}", ConsoleColor.Green);
                }

                Console.WriteLine($"  Commit message: {repo.SuggestedCommitMessage}");
                if (repo.GitAddCommand is not null)
                {
                    WriteColored($"  {repo.GitAddCommand}", ConsoleColor.DarkGray);
                }
            }
        }

        Console.WriteLine();
        var changed = report.ChangedRepositories.Count();
        WriteColored(
            changed == 0
                ? "No package files were changed."
                : $"{changed} repositor{(changed == 1 ? "y has" : "ies have")} changes to review, commit and push.",
            changed == 0 ? ConsoleColor.Gray : ConsoleColor.Cyan);
    }

    public static ConsoleColor StatusColor(ProjectStatus status) => status switch
    {
        ProjectStatus.Clean or ProjectStatus.Fixed => ConsoleColor.Green,
        ProjectStatus.Vulnerable or ProjectStatus.Error => ConsoleColor.Red,
        ProjectStatus.Partial or ProjectStatus.WouldFix or ProjectStatus.Unchanged => ConsoleColor.Yellow,
        ProjectStatus.Skipped => ConsoleColor.DarkYellow,
        _ => ConsoleColor.Gray,
    };

    public static void WriteColored(string text, ConsoleColor color, bool toError = false)
    {
        lock (Gate)
        {
            var writer = toError ? Console.Error : Console.Out;
            var useColor = !(toError ? Console.IsErrorRedirected : Console.IsOutputRedirected)
                && Environment.GetEnvironmentVariable("NO_COLOR") is null;
            if (useColor)
            {
                Console.ForegroundColor = color;
            }

            writer.WriteLine(text);
            if (useColor)
            {
                Console.ResetColor();
            }
        }
    }

    public static void Error(string message) => WriteColored(message, ConsoleColor.Red, toError: true);

    private static string Num(int? n) => n is null ? "-" : n.Value.ToString();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Synchronous progress sink that prints one line per status change to stderr.</summary>
internal sealed class ConsoleProgress(bool quiet) : IProgress<SweepEvent>
{
    public void Report(SweepEvent e)
    {
        if (quiet || e.IsOutputLine || e.Status == ProjectStatus.Queued)
        {
            return;
        }

        var text = e.Result is null
            ? $"  … {e.Project.Name}: {e.Message ?? e.Status.ToString()}"
            : $"  {Symbol(e.Status)} {e.Project.Name}: {e.Status}{(e.Message is null ? "" : " — " + e.Message)}";
        ConsoleUi.WriteColored(text, e.Result is null ? ConsoleColor.DarkGray : ConsoleUi.StatusColor(e.Status), toError: true);
    }

    private static string Symbol(ProjectStatus s) => s switch
    {
        ProjectStatus.Clean or ProjectStatus.Fixed => "✓",
        ProjectStatus.Error => "✗",
        ProjectStatus.Skipped => "-",
        _ => "!",
    };
}
