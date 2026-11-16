using System.Security.Cryptography;
using DepSweep.Core.Config;
using DepSweep.Core.Discovery;
using DepSweep.Core.Git;
using DepSweep.Core.Models;
using DepSweep.Core.PackageManagers;
using DepSweep.Core.Processes;
using DepSweep.Core.Reporting;

namespace DepSweep.Core.Sweep;

public sealed record SweepOptions
{
    /// <summary>Allow breaking (semver-major) fixes: npm audit fix --force / pnpm overrides.</summary>
    public bool Force { get; init; }

    /// <summary>Fix projects whose package files already have uncommitted changes.</summary>
    public bool AllowDirty { get; init; }

    /// <summary>Audit only and report what would be fixed; never modifies files.</summary>
    public bool DryRun { get; init; }

    public int Concurrency { get; init; } = 4;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public string CommitMessageTemplate { get; init; } = DepSweepConfig.DefaultCommitMessageTemplate;

    public static SweepOptions FromConfig(DepSweepConfig config) => new()
    {
        Concurrency = Math.Max(1, config.Concurrency),
        Timeout = TimeSpan.FromSeconds(Math.Max(10, config.TimeoutSeconds)),
        CommitMessageTemplate = string.IsNullOrWhiteSpace(config.CommitMessageTemplate)
            ? DepSweepConfig.DefaultCommitMessageTemplate
            : config.CommitMessageTemplate!,
    };
}

/// <summary>Progress notification. Either a status change (with <see cref="Result"/> once final) or an output line.</summary>
public sealed record SweepEvent(NodeProject Project, ProjectStatus Status, string? Message = null, ProjectResult? Result = null)
{
    public bool IsOutputLine { get; init; }
}

/// <summary>The engine shared by the CLI and the GUI: discover, audit, fix, report.</summary>
public sealed class SweepService
{
    private readonly PackageManagerRegistry _packageManagers;
    private readonly GitInspector _git;

    public SweepService(IProcessRunner runner)
        : this(PackageManagerRegistry.CreateDefault(runner), new GitInspector(runner))
    {
    }

    public SweepService(PackageManagerRegistry packageManagers, GitInspector git)
    {
        _packageManagers = packageManagers;
        _git = git;
    }

    public static DiscoveryResult Discover(DepSweepConfig config, IEnumerable<RootFolder>? roots = null, CancellationToken ct = default) =>
        new ProjectDiscovery(config.ExcludedDirectoryNames).Discover(roots ?? config.Roots, ct);

    /// <summary>Audits every project (in parallel) without modifying anything.</summary>
    public Task<IReadOnlyList<ProjectResult>> ScanAsync(
        IReadOnlyList<NodeProject> projects,
        SweepOptions options,
        IProgress<SweepEvent>? progress = null,
        CancellationToken ct = default) =>
        RunAllAsync(projects, options, progress, ct, ScanOneAsync);

    /// <summary>Audits and fixes every project, then builds a report grouped by git repository.</summary>
    public async Task<SweepReport> FixAsync(
        IReadOnlyList<NodeProject> projects,
        SweepOptions options,
        IProgress<SweepEvent>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var results = await RunAllAsync(projects, options, progress, ct, FixOneAsync);
        return SweepReport.Create(results, options, started, DateTimeOffset.Now, ct.IsCancellationRequested);
    }

    private async Task<IReadOnlyList<ProjectResult>> RunAllAsync(
        IReadOnlyList<NodeProject> projects,
        SweepOptions options,
        IProgress<SweepEvent>? progress,
        CancellationToken ct,
        Func<NodeProject, SweepOptions, IProgress<SweepEvent>?, CancellationToken, Task<ProjectResult>> work)
    {
        foreach (var p in projects)
        {
            progress?.Report(new SweepEvent(p, ProjectStatus.Queued));
        }

        using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var tasks = projects.Select(async project =>
        {
            ProjectResult result;
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    result = await work(project, options, progress, ct);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = new ProjectResult { Project = project, Status = ProjectStatus.Skipped, Message = "Cancelled" };
            }
            catch (Exception ex)
            {
                result = new ProjectResult { Project = project, Status = ProjectStatus.Error, Message = DescribeError(ex) };
            }

            progress?.Report(new SweepEvent(project, result.Status, result.Message, result));
            return result;
        });

        return await Task.WhenAll(tasks);
    }

    private async Task<ProjectResult> ScanOneAsync(NodeProject project, SweepOptions options, IProgress<SweepEvent>? progress, CancellationToken ct)
    {
        progress?.Report(new SweepEvent(project, ProjectStatus.Scanning, "Auditing"));
        var git = await _git.InspectAsync(project, ct);
        try
        {
            var audit = await _packageManagers.For(project.PackageManager).AuditAsync(project, new CommandContext(options.Timeout, null, ct));
            return new ProjectResult
            {
                Project = project,
                Git = git,
                Before = audit,
                Status = audit.IsClean ? ProjectStatus.Clean : ProjectStatus.Vulnerable,
                Message = audit.IsClean ? "No vulnerabilities" : DescribeFixability(audit, options.Force),
            };
        }
        catch (Exception ex) when (ex is AuditFailedException or ToolNotFoundException or TimeoutException)
        {
            return new ProjectResult { Project = project, Git = git, Status = ProjectStatus.Error, Message = DescribeError(ex) };
        }
    }

    private async Task<ProjectResult> FixOneAsync(NodeProject project, SweepOptions options, IProgress<SweepEvent>? progress, CancellationToken ct)
    {
        var adapter = _packageManagers.For(project.PackageManager);
        var context = new CommandContext(
            options.Timeout,
            line => progress?.Report(new SweepEvent(project, ProjectStatus.Fixing, line) { IsOutputLine = true }),
            ct);

        var scan = await ScanOneAsync(project, options, progress, ct);
        if (scan.Status != ProjectStatus.Vulnerable)
        {
            return scan;
        }

        var before = scan.Before!;
        if (scan.Git is { DirtyPackageFiles.Count: > 0 } git && !options.AllowDirty)
        {
            return scan with
            {
                Status = ProjectStatus.Skipped,
                Message = $"Package files have uncommitted changes ({string.Join(", ", git.DirtyPackageFiles)}); commit or stash them, or use --allow-dirty",
            };
        }

        if (options.DryRun)
        {
            var wouldFix = before.Vulnerabilities.Any(v => v.Fix == FixKind.Fixable || (options.Force && v.Fix == FixKind.NeedsForce));
            return scan with
            {
                Status = wouldFix ? ProjectStatus.WouldFix : ProjectStatus.Unchanged,
                Message = "Dry run: " + DescribeFixability(before, options.Force),
            };
        }

        progress?.Report(new SweepEvent(project, ProjectStatus.Fixing, options.Force ? "Fixing (--force)" : "Fixing"));
        var snapshot = SnapshotFiles(project);
        var fix = await adapter.FixAsync(project, options.Force, context);
        var changed = await ChangedFilesAsync(project, snapshot, scan.Git, ct);

        progress?.Report(new SweepEvent(project, ProjectStatus.Scanning, "Re-auditing"));
        var after = await adapter.AuditAsync(project, new CommandContext(options.Timeout, null, ct));

        var reduced = before.Counts.Reduction(after.Counts);
        var status = after.IsClean
            ? ProjectStatus.Fixed
            : reduced.Total > 0
                ? ProjectStatus.Partial
                : ProjectStatus.Unchanged;

        var messages = new List<string>();
        if (status == ProjectStatus.Fixed)
        {
            messages.Add($"Fixed {before.Counts.ToSummary()}");
        }
        else
        {
            if (status == ProjectStatus.Partial)
            {
                messages.Add($"Fixed {reduced.ToSummary()}");
            }
            else if (changed.Count > 0)
            {
                messages.Add("Package files changed but no vulnerabilities were fixed; review before committing");
            }

            messages.Add("Remaining: " + DescribeFixability(after, options.Force));
            // A non-zero exit code is normal while vulnerabilities remain; only surface real error lines.
            var errorLines = fix.OutputTail
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Contains("ERR", StringComparison.Ordinal) || l.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fix.ExitCode != 0 && errorLines.Count > 0)
            {
                messages.Add("Fix command error: " + string.Join(" | ", errorLines));
            }
        }

        if (fix.Note is not null)
        {
            messages.Add(fix.Note);
        }

        return scan with { After = after, ChangedFiles = changed, Status = status, Message = string.Join(". ", messages) };
    }

    /// <summary>Summary like "2 fixable, 1 needs --force, 3 without a fix".</summary>
    public static string DescribeFixability(AuditResult audit, bool force)
    {
        var fixable = audit.Vulnerabilities.Count(v => v.Fix == FixKind.Fixable);
        var needsForce = audit.Vulnerabilities.Count(v => v.Fix == FixKind.NeedsForce);
        var noFix = audit.Vulnerabilities.Count(v => v.Fix == FixKind.NoFix);
        var parts = new List<string>();
        if (fixable > 0)
        {
            parts.Add($"{fixable} fixable");
        }

        if (needsForce > 0)
        {
            parts.Add(force ? $"{needsForce} breaking (force)" : $"{needsForce} need --force");
        }

        if (noFix > 0)
        {
            parts.Add($"{noFix} without a fix");
        }

        return $"{audit.Counts.ToSummary()}" + (parts.Count > 0 ? $" — {string.Join(", ", parts)}" : "");
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        ToolNotFoundException t => $"Tool not found: {t.Tool} is not installed or not on PATH",
        AuditFailedException a => a.Message,
        TimeoutException t => t.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static Dictionary<string, string?> SnapshotFiles(NodeProject project) =>
        project.ManifestFiles
            .Select(f => Path.Combine(project.Path, f))
            .ToDictionary(f => f, HashFile, PathUtil.Comparer);

    /// <summary>
    /// Files whose bytes changed during the fix. Inside a git repo a file must also show as modified in
    /// <c>git status</c>, so rewrites git normalizes away (e.g. LF vs CRLF with autocrlf) are not reported.
    /// </summary>
    private async Task<IReadOnlyList<string>> ChangedFilesAsync(
        NodeProject project, Dictionary<string, string?> before, GitInfo? git, CancellationToken ct)
    {
        var changed = before.Where(kv => HashFile(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        if (git is null || changed.Count == 0)
        {
            return changed;
        }

        var modified = (await _git.GetChangedPackageFilesAsync(project, ct))
            .Select(rel => PathUtil.Normalize(Path.Combine(git.RepoRoot, rel)))
            .ToHashSet(PathUtil.Comparer);
        return changed.Where(f => modified.Contains(PathUtil.Normalize(f))).ToList();
    }

    private static string? HashFile(string path)
    {
        try
        {
            return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
        }
        catch (IOException)
        {
            return "unreadable";
        }
    }
}
