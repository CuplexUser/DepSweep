using DepSweep.Core.Models;
using DepSweep.Core.Processes;

namespace DepSweep.Core.Git;

/// <summary>Read-only git queries. DepSweep never stages, commits or pushes.</summary>
public sealed class GitInspector(IProcessRunner runner)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Returns git context for <paramref name="project"/>, or null when it is not inside a git work tree.</summary>
    public async Task<GitInfo?> InspectAsync(NodeProject project, CancellationToken ct = default)
    {
        ProcessResult top;
        try
        {
            top = await runner.RunAsync("git", ["rev-parse", "--show-toplevel"], project.Path, Timeout, null, ct);
        }
        catch (ToolNotFoundException)
        {
            return null;
        }

        if (!top.Succeeded || string.IsNullOrWhiteSpace(top.StandardOutput))
        {
            return null;
        }

        var repoRoot = Path.GetFullPath(top.StandardOutput.Trim());

        // symbolic-ref also works on an unborn branch (no commits yet); it fails when HEAD is detached.
        var branchResult = await runner.RunAsync("git", ["symbolic-ref", "--short", "-q", "HEAD"], project.Path, Timeout, null, ct);
        var branch = branchResult.Succeeded && branchResult.StandardOutput.Trim().Length > 0
            ? branchResult.StandardOutput.Trim()
            : "(detached)";

        var dirty = await GetChangedPackageFilesAsync(project, ct);
        return new GitInfo(repoRoot, branch, dirty);
    }

    /// <summary>
    /// Package files of <paramref name="project"/> whose content differs from HEAD (plus new untracked ones),
    /// relative to the repo root. Content-based, so line-ending-only rewrites that git normalizes are ignored
    /// (unlike <c>git status</c>, which can flag them).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetChangedPackageFilesAsync(NodeProject project, CancellationToken ct = default)
    {
        string[] diffArgs = ["diff", "--name-only", "-z", "HEAD", "--", .. project.ManifestFiles];
        var diff = await runner.RunAsync("git", diffArgs, project.Path, Timeout, null, ct);
        if (!diff.Succeeded)
        {
            // No commits yet (unborn HEAD): fall back to status.
            string[] statusArgs = ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", .. project.ManifestFiles];
            var status = await runner.RunAsync("git", statusArgs, project.Path, Timeout, null, ct);
            return status.Succeeded ? ParsePorcelainZ(status.StandardOutput) : [];
        }

        string[] untrackedArgs = ["ls-files", "--others", "--exclude-standard", "--full-name", "-z", "--", .. project.ManifestFiles];
        var untracked = await runner.RunAsync("git", untrackedArgs, project.Path, Timeout, null, ct);

        return SplitZ(diff.StandardOutput)
            .Concat(untracked.Succeeded ? SplitZ(untracked.StandardOutput) : [])
            .Where(p => !p.Contains("node_modules/", StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    private static IEnumerable<string> SplitZ(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Parses <c>git status --porcelain=v1 -z</c> output into repo-relative paths.</summary>
    internal static IReadOnlyList<string> ParsePorcelainZ(string output)
    {
        var paths = new List<string>();
        var entries = output.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            paths.Add(entry[3..]);

            // Renames and copies are followed by the original path as a separate entry.
            if (entry[0] is 'R' or 'C')
            {
                i++;
            }
        }

        return paths.Where(p => !p.Contains("node_modules/", StringComparison.Ordinal)).Distinct().ToList();
    }
}
