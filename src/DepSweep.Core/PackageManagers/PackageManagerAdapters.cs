using DepSweep.Core.Models;
using DepSweep.Core.Processes;

namespace DepSweep.Core.PackageManagers;

/// <summary>Per-call settings for running a package manager command.</summary>
public sealed record CommandContext(TimeSpan Timeout, Action<string>? OnOutput = null, CancellationToken CancellationToken = default);

/// <summary>Result of running a fix command. The real effect is measured by re-auditing afterwards.</summary>
public sealed record FixRunResult(int ExitCode, string OutputTail, string? Note = null);

public interface IPackageManagerAdapter
{
    PackageManagerKind Kind { get; }

    Task<AuditResult> AuditAsync(NodeProject project, CommandContext context);

    Task<FixRunResult> FixAsync(NodeProject project, bool force, CommandContext context);
}

public sealed class NpmAdapter(IProcessRunner runner) : IPackageManagerAdapter
{
    public PackageManagerKind Kind => PackageManagerKind.Npm;

    public async Task<AuditResult> AuditAsync(NodeProject project, CommandContext ctx)
    {
        // Exit code is 1 whenever vulnerabilities exist, so success is decided by the JSON payload.
        var result = await runner.RunAsync("npm", ["audit", "--json"], project.Path, ctx.Timeout, null, ctx.CancellationToken);
        return NpmAuditParser.Parse(
            result.StandardOutput.Length > 0 ? result.StandardOutput : result.StandardError,
            PackageJson.ReadDeclaredDependencies(project.Path));
    }

    public async Task<FixRunResult> FixAsync(NodeProject project, bool force, CommandContext ctx)
    {
        string[] args = force ? ["audit", "fix", "--force"] : ["audit", "fix"];
        var result = await runner.RunAsync("npm", args, project.Path, ctx.Timeout, ctx.OnOutput, ctx.CancellationToken);
        return new FixRunResult(result.ExitCode, result.Tail());
    }
}

public sealed class PnpmAdapter(IProcessRunner runner) : IPackageManagerAdapter
{
    private bool? _supportsFixMethods;

    public PackageManagerKind Kind => PackageManagerKind.Pnpm;

    public async Task<AuditResult> AuditAsync(NodeProject project, CommandContext ctx)
    {
        var result = await runner.RunAsync("pnpm", ["audit", "--json"], project.Path, ctx.Timeout, null, ctx.CancellationToken);
        return PnpmAuditParser.Parse(
            result.StandardOutput.Length > 0 ? result.StandardOutput : result.StandardError,
            PackageJson.ReadDeclaredDependencies(project.Path));
    }

    public async Task<FixRunResult> FixAsync(NodeProject project, bool force, CommandContext ctx)
    {
        string[] args;
        string? note = null;
        if (await SupportsFixMethodsAsync(project, ctx))
        {
            // "update" re-resolves the lockfile within declared ranges (safe);
            // "override" pins patched versions via overrides in pnpm-workspace.yaml (may be breaking).
            args = ["audit", "--fix", force ? "override" : "update"];
        }
        else
        {
            args = ["audit", "--fix"];
            if (!force)
            {
                note = "This pnpm version only supports override-based fixes; overrides were added.";
            }
        }

        var fix = await runner.RunAsync("pnpm", args, project.Path, ctx.Timeout, ctx.OnOutput, ctx.CancellationToken);
        if (!fix.Succeeded)
        {
            return new FixRunResult(fix.ExitCode, fix.Tail(), note);
        }

        // Apply overrides / sync node_modules with the updated lockfile.
        var install = await runner.RunAsync("pnpm", ["install", "--no-frozen-lockfile"], project.Path, ctx.Timeout, ctx.OnOutput, ctx.CancellationToken);
        return new FixRunResult(install.ExitCode, install.Tail(), note);
    }

    private async Task<bool> SupportsFixMethodsAsync(NodeProject project, CommandContext ctx)
    {
        if (_supportsFixMethods is { } cached)
        {
            return cached;
        }

        var help = await runner.RunAsync("pnpm", ["audit", "--help"], project.Path, ctx.Timeout, null, ctx.CancellationToken);
        var supported = help.StandardOutput.Contains("\"update\"", StringComparison.Ordinal);
        _supportsFixMethods = supported;
        return supported;
    }
}

public sealed class PackageManagerRegistry(IEnumerable<IPackageManagerAdapter> adapters)
{
    private readonly Dictionary<PackageManagerKind, IPackageManagerAdapter> _adapters = adapters.ToDictionary(a => a.Kind);

    public static PackageManagerRegistry CreateDefault(IProcessRunner runner) =>
        new([new NpmAdapter(runner), new PnpmAdapter(runner)]);

    public IPackageManagerAdapter For(PackageManagerKind kind) => _adapters[kind];
}
