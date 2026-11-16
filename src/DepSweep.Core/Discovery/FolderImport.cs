using DepSweep.Core.Config;
using DepSweep.Core.Models;

namespace DepSweep.Core.Discovery;

/// <summary>A project found while browsing for folders to import.</summary>
/// <param name="AlreadyIncluded">True when an existing enabled root already covers this project.</param>
public sealed record ImportCandidate(NodeProject Project, string? RepoRoot, bool AlreadyIncluded);

/// <summary>Helpers for adding many folders/projects to the configuration at once.</summary>
public static class FolderImport
{
    /// <summary>
    /// Builds the root entry for a folder the user picked: a folder that is itself a project is added
    /// as that project only; any other folder is searched recursively.
    /// </summary>
    public static RootFolder ToRoot(string folder)
    {
        var path = PathUtil.Normalize(folder);
        return new RootFolder
        {
            Path = path,
            Recursive = ProjectDiscovery.DetectPackageManager(path, out _) is null
                || !File.Exists(Path.Combine(path, "package.json")),
        };
    }

    /// <summary>
    /// Adds roots that are not already configured. Returns the roots actually added.
    /// Dropped files (e.g. a package.json) are mapped to their folder.
    /// </summary>
    public static IReadOnlyList<RootFolder> AddRoots(DepSweepConfig config, IEnumerable<RootFolder> roots)
    {
        var added = new List<RootFolder>();
        foreach (var root in roots)
        {
            var path = PathUtil.Normalize(File.Exists(root.Path) ? Path.GetDirectoryName(root.Path)! : root.Path);
            if (!Directory.Exists(path) || config.FindRoot(path) is not null)
            {
                continue;
            }

            var entry = new RootFolder { Path = path, Recursive = root.Recursive, Enabled = true };
            config.Roots.Add(entry);
            config.SetExcluded(path, false);
            added.Add(entry);
        }

        return added;
    }

    /// <summary>Finds projects under <paramref name="folders"/> and flags those already covered by <paramref name="config"/>.</summary>
    public static IReadOnlyList<ImportCandidate> FindCandidates(
        DepSweepConfig config, IEnumerable<string> folders, CancellationToken ct = default)
    {
        var discovery = new ProjectDiscovery(config.ExcludedDirectoryNames);
        var found = discovery.Discover(folders.Select(f => new RootFolder { Path = f, Recursive = true }), ct);
        return found.Projects
            .Select(p => new ImportCandidate(p, GitRepoLocator.FindRepoRoot(p.Path), IsCovered(config, p.Path)))
            .ToList();
    }

    /// <summary>True when an enabled configured root would discover the project at <paramref name="projectPath"/>.</summary>
    public static bool IsCovered(DepSweepConfig config, string projectPath) =>
        config.Roots.Any(r => r.Enabled
            && (PathUtil.Equal(r.Path, projectPath) || (r.Recursive && PathUtil.IsUnder(projectPath, r.Path))));
}

/// <summary>Cheap, process-free git lookups used for display before any scan runs.</summary>
public static class GitRepoLocator
{
    /// <summary>Walks up from <paramref name="path"/> to the folder containing ".git" (directory or worktree file).</summary>
    public static string? FindRepoRoot(string path)
    {
        try
        {
            for (var dir = new DirectoryInfo(PathUtil.Normalize(path)); dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    return dir.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Treat unreadable paths as "not in a repository".
        }

        return null;
    }

    /// <summary>Reads the current branch from .git/HEAD; "(detached)" for a detached HEAD; null when unknown.</summary>
    public static string? ReadBranch(string repoRoot)
    {
        try
        {
            var gitPath = Path.Combine(repoRoot, ".git");
            var gitDir = gitPath;
            if (File.Exists(gitPath))
            {
                // Worktree or submodule: ".git" is a file containing "gitdir: <path>".
                var line = File.ReadAllText(gitPath).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return null;
                }

                gitDir = Path.GetFullPath(Path.Combine(repoRoot, line["gitdir:".Length..].Trim()));
            }

            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            const string prefix = "ref: refs/heads/";
            return head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : "(detached)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
