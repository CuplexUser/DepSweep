namespace DepSweep.Core.Models;

/// <summary>A folder containing a package.json and a supported lockfile.</summary>
public sealed record NodeProject(
    string Path,
    string Name,
    PackageManagerKind PackageManager,
    string RootPath)
{
    /// <summary>Path relative to the configured root folder ("." when the project is the root).</summary>
    public string RelativePath => System.IO.Path.GetRelativePath(RootPath, Path).Replace('\\', '/');

    public string LockFileName => PackageManager == PackageManagerKind.Pnpm ? "pnpm-lock.yaml" : "package-lock.json";

    /// <summary>Files that an audit fix may modify, relative to <see cref="Path"/>.</summary>
    public IReadOnlyList<string> ManifestFiles => PackageManager == PackageManagerKind.Pnpm
        ? ["package.json", "pnpm-lock.yaml", "pnpm-workspace.yaml"]
        : ["package.json", "package-lock.json", "npm-shrinkwrap.json"];
}

/// <summary>A folder with a package.json that cannot be audited, with the reason why.</summary>
public sealed record IgnoredFolder(string Path, string Reason);

/// <summary>Git context for a project folder.</summary>
/// <param name="RepoRoot">Repository top-level directory.</param>
/// <param name="Branch">Current branch, or "(detached)".</param>
/// <param name="DirtyPackageFiles">Package files with uncommitted changes, relative to the repo root.</param>
public sealed record GitInfo(string RepoRoot, string Branch, IReadOnlyList<string> DirtyPackageFiles);

/// <summary>The outcome of auditing (and optionally fixing) a single project.</summary>
public sealed record ProjectResult
{
    public required NodeProject Project { get; init; }
    public required ProjectStatus Status { get; init; }
    public GitInfo? Git { get; init; }
    public AuditResult? Before { get; init; }
    public AuditResult? After { get; init; }

    /// <summary>Changed files as absolute paths.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string? Message { get; init; }

    /// <summary>The latest known audit (after a fix if one ran, otherwise before).</summary>
    public AuditResult? Current => After ?? Before;

    public SeverityCounts FixedCounts =>
        Before is null || After is null ? SeverityCounts.Empty : Before.Counts.Reduction(After.Counts);
}
