using System.Text.Json;
using DepSweep.Core.Config;
using DepSweep.Core.Models;

namespace DepSweep.Core.Discovery;

public sealed record DiscoveryResult(IReadOnlyList<NodeProject> Projects, IReadOnlyList<IgnoredFolder> Ignored);

/// <summary>
/// Finds auditable projects under the configured roots. A folder is an audit target only when it holds a
/// package.json plus a supported lockfile, so workspace members (which share the root lockfile) are covered
/// by auditing the workspace root.
/// </summary>
public sealed class ProjectDiscovery
{
    private readonly HashSet<string> _excludedNames;

    public ProjectDiscovery(IEnumerable<string>? excludedDirectoryNames = null)
    {
        _excludedNames = new HashSet<string>(
            excludedDirectoryNames ?? DepSweepConfig.DefaultExcludedDirectories,
            StringComparer.OrdinalIgnoreCase);
    }

    public DiscoveryResult Discover(IEnumerable<RootFolder> roots, CancellationToken cancellationToken = default)
    {
        var projects = new List<NodeProject>();
        var ignored = new List<IgnoredFolder>();
        var seen = new HashSet<string>(PathUtil.Comparer);

        foreach (var root in roots.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Path)))
        {
            var rootPath = PathUtil.Normalize(root.Path);
            if (!Directory.Exists(rootPath))
            {
                ignored.Add(new IgnoredFolder(rootPath, "Root folder does not exist"));
                continue;
            }

            Walk(rootPath, rootPath, root.Recursive, insideProject: false, projects, ignored, seen, cancellationToken);
        }

        return new DiscoveryResult(
            projects.OrderBy(p => p.Path, PathUtil.Comparer).ToList(),
            ignored.OrderBy(i => i.Path, PathUtil.Comparer).ToList());
    }

    private void Walk(
        string dir,
        string rootPath,
        bool recursive,
        bool insideProject,
        List<NodeProject> projects,
        List<IgnoredFolder> ignored,
        HashSet<string> seen,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var isProject = false;
        if (File.Exists(Path.Combine(dir, "package.json")))
        {
            var kind = DetectPackageManager(dir, out var reason);
            if (kind is { } pm)
            {
                isProject = true;
                if (seen.Add(dir))
                {
                    projects.Add(new NodeProject(dir, ReadName(dir), pm, rootPath));
                }
            }
            else if (!insideProject && seen.Add(dir))
            {
                // Folders inside a project are typically workspace members covered by the parent lockfile.
                ignored.Add(new IgnoredFolder(dir, reason!));
            }
        }

        if (!recursive)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || _excludedNames.Contains(name))
            {
                continue;
            }

            try
            {
                // Skip junctions/symlinks to avoid cycles and duplicate visits.
                if (new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            Walk(child, rootPath, recursive, insideProject || isProject, projects, ignored, seen, ct);
        }
    }

    internal static PackageManagerKind? DetectPackageManager(string dir, out string? reason)
    {
        reason = null;
        var hasNpmLock = File.Exists(Path.Combine(dir, "package-lock.json"))
            || File.Exists(Path.Combine(dir, "npm-shrinkwrap.json"));
        var hasPnpmLock = File.Exists(Path.Combine(dir, "pnpm-lock.yaml"));

        if (hasNpmLock && hasPnpmLock)
        {
            var declared = ReadPackageManagerField(dir);
            return declared?.StartsWith("npm", StringComparison.OrdinalIgnoreCase) == true
                ? PackageManagerKind.Npm
                : PackageManagerKind.Pnpm;
        }

        if (hasPnpmLock)
        {
            return PackageManagerKind.Pnpm;
        }

        if (hasNpmLock)
        {
            return PackageManagerKind.Npm;
        }

        reason = File.Exists(Path.Combine(dir, "yarn.lock")) || File.Exists(Path.Combine(dir, "bun.lockb"))
            ? "Unsupported package manager (yarn/bun lockfile)"
            : "No lockfile (run npm install or pnpm install first)";
        return null;
    }

    private static string ReadName(string dir)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(dir, "package.json"));
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                return name.GetString()!;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to the folder name.
        }

        return Path.GetFileName(dir);
    }

    private static string? ReadPackageManagerField(string dir)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(dir, "package.json"));
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("packageManager", out var pm) && pm.ValueKind == JsonValueKind.String
                ? pm.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
