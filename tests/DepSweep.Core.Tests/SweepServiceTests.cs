using DepSweep.Core.Git;
using DepSweep.Core.Models;
using DepSweep.Core.PackageManagers;
using DepSweep.Core.Processes;
using DepSweep.Core.Reporting;
using DepSweep.Core.Sweep;

namespace DepSweep.Core.Tests;

public sealed class SweepServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "depsweep-sweep-" + Guid.NewGuid().ToString("N"));

    public SweepServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly AuditResult Vulnerable = new(
        new SeverityCounts(0, 0, 1, 1, 0),
        [
            new Vulnerability("lodash", Severity.High, true, FixKind.Fixable, "lodash@4.18.1", []),
            new Vulnerability("request", Severity.Moderate, true, FixKind.NoFix, null, []),
        ]);

    private static readonly AuditResult OnlyUnfixable = new(
        new SeverityCounts(0, 0, 1, 0, 0),
        [new Vulnerability("request", Severity.Moderate, true, FixKind.NoFix, null, [])]);

    private NodeProject MakeProject(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "package-lock.json"), "v1");
        return new NodeProject(dir, name, PackageManagerKind.Npm, _root);
    }

    private static SweepService CreateService(FakeAdapter adapter, FakeGitRunner? git = null) =>
        new(new PackageManagerRegistry([adapter]), new GitInspector(git ?? new FakeGitRunner(null)));

    [Fact]
    public async Task Fix_updates_lockfile_and_reports_partial_with_remaining_issues()
    {
        var project = MakeProject("app");
        var adapter = new FakeAdapter(Vulnerable, OnlyUnfixable, writeLockfile: true);
        var git = new FakeGitRunner(_root);

        var report = await CreateService(adapter, git).FixAsync([project], new SweepOptions());

        var result = report.Projects.Single();
        result.Status.ShouldBe(ProjectStatus.Partial);
        result.ChangedFiles.ShouldBe([Path.Combine(project.Path, "package-lock.json")]);
        result.FixedCounts.ShouldBe(new SeverityCounts(0, 0, 0, 1, 0));
        result.Message.ShouldNotBeNull().ShouldContain("without a fix");

        var repo = report.Repositories.Single();
        repo.RepoRoot.ShouldBe(_root);
        repo.Branch.ShouldBe("main");
        repo.ChangedFiles.ShouldBe(["app/package-lock.json"]);
        repo.SuggestedCommitMessage.ShouldBe("chore(deps): fix audit vulnerabilities (1 high)");
        repo.GitAddCommand.ShouldNotBeNull().ShouldEndWith("add -- app/package-lock.json");

        MarkdownReportWriter.Write(report).ShouldContain("## Repositories to commit");
        JsonReportWriter.Write(report).ShouldContain("\"suggestedCommitMessage\"");
    }

    [Fact]
    public async Task Lockfile_change_without_fixes_is_unchanged_with_neutral_commit_message()
    {
        var project = MakeProject("churn");
        var adapter = new FakeAdapter(OnlyUnfixable, OnlyUnfixable, writeLockfile: true);

        var report = await CreateService(adapter, new FakeGitRunner(_root)).FixAsync([project], new SweepOptions());

        var result = report.Projects.Single();
        result.Status.ShouldBe(ProjectStatus.Unchanged);
        result.Message.ShouldNotBeNull().ShouldContain("review before committing");
        report.Repositories.Single().HasChanges.ShouldBeTrue();
        report.Repositories.Single().SuggestedCommitMessage.ShouldBe("chore(deps): update lockfile via audit fix");
    }

    [Fact]
    public async Task Byte_changes_that_git_does_not_see_are_not_reported()
    {
        var project = MakeProject("eol");
        var adapter = new FakeAdapter(Vulnerable, Vulnerable, writeLockfile: true) { LockfileContent = "v1\r" };

        var report = await CreateService(adapter, new FakeGitRunner(_root)).FixAsync([project], new SweepOptions());

        report.Projects.Single().ChangedFiles.ShouldBeEmpty();
        report.Projects.Single().Status.ShouldBe(ProjectStatus.Unchanged);
        report.ChangedRepositories.ShouldBeEmpty();
    }

    [Fact]
    public void Config_does_not_persist_the_default_commit_template()
    {
        var path = Path.Combine(_root, "config.json");
        var store = new DepSweep.Core.Config.ConfigStore(path);
        store.Save(new DepSweep.Core.Config.DepSweepConfig());

        File.ReadAllText(path).ShouldNotContain("commitMessageTemplate");
        SweepOptions.FromConfig(store.Load()).CommitMessageTemplate.ShouldBe(DepSweep.Core.Config.DepSweepConfig.DefaultCommitMessageTemplate);
    }

    [Fact]
    public async Task Fix_skips_projects_with_dirty_package_files_unless_allowed()
    {
        var project = MakeProject("dirty");
        var git = new FakeGitRunner(_root) { DirtyFiles = ["dirty/package.json"] };

        var skipped = await CreateService(new FakeAdapter(Vulnerable, AuditResult.Clean, writeLockfile: true), git)
            .FixAsync([project], new SweepOptions());
        skipped.Projects.Single().Status.ShouldBe(ProjectStatus.Skipped);
        skipped.Projects.Single().Message.ShouldNotBeNull().ShouldContain("uncommitted");
        File.ReadAllText(Path.Combine(project.Path, "package-lock.json")).ShouldBe("v1");

        var allowed = await CreateService(new FakeAdapter(Vulnerable, AuditResult.Clean, writeLockfile: true), git)
            .FixAsync([project], new SweepOptions { AllowDirty = true });
        allowed.Projects.Single().Status.ShouldBe(ProjectStatus.Fixed);
    }

    [Fact]
    public async Task Dry_run_never_calls_fix()
    {
        var project = MakeProject("dry");
        var adapter = new FakeAdapter(Vulnerable, AuditResult.Clean, writeLockfile: true);

        var report = await CreateService(adapter).FixAsync([project], new SweepOptions { DryRun = true });

        adapter.FixCalls.ShouldBe(0);
        report.Projects.Single().Status.ShouldBe(ProjectStatus.WouldFix);
        report.Repositories.Single().RepoRoot.ShouldBeNull();
    }

    [Fact]
    public async Task Clean_projects_are_not_fixed_and_errors_are_isolated()
    {
        var clean = MakeProject("clean");
        var broken = MakeProject("broken");
        var adapter = new FakeAdapter(AuditResult.Clean, AuditResult.Clean, writeLockfile: false) { FailFor = broken.Path };

        var results = await CreateService(adapter).FixAsync([clean, broken], new SweepOptions());

        adapter.FixCalls.ShouldBe(0);
        results.Projects.Single(p => p.Project == clean).Status.ShouldBe(ProjectStatus.Clean);
        results.Projects.Single(p => p.Project == broken).Status.ShouldBe(ProjectStatus.Error);
        results.Projects.Single(p => p.Project == broken).Message.ShouldNotBeNull().ShouldContain("registry down");
        results.ChangedRepositories.ShouldBeEmpty();
    }

    [Fact]
    public void Porcelain_z_output_is_parsed_including_renames()
    {
        var output = " M web/package.json\0?? web/pnpm-workspace.yaml\0R  new.json\0old.json\0 M x/node_modules/a/package.json\0";
        GitInspector.ParsePorcelainZ(output).ShouldBe(["web/package.json", "web/pnpm-workspace.yaml", "new.json"]);
    }

    private sealed class FakeAdapter(AuditResult before, AuditResult after, bool writeLockfile) : IPackageManagerAdapter
    {
        public int FixCalls { get; private set; }

        public string? FailFor { get; init; }

        public string LockfileContent { get; init; } = "v2";

        public PackageManagerKind Kind => PackageManagerKind.Npm;

        public Task<AuditResult> AuditAsync(NodeProject project, CommandContext context)
        {
            if (project.Path == FailFor)
            {
                throw new AuditFailedException("npm audit failed: registry down");
            }

            return Task.FromResult(FixCalls > 0 ? after : before);
        }

        public Task<FixRunResult> FixAsync(NodeProject project, bool force, CommandContext context)
        {
            FixCalls++;
            if (writeLockfile)
            {
                File.WriteAllText(Path.Combine(project.Path, "package-lock.json"), LockfileContent);
            }

            return Task.FromResult(new FixRunResult(0, ""));
        }
    }

    /// <summary>Simulates git: a repo at <paramref name="repoRoot"/> on branch "main", or no repo when null.</summary>
    private sealed class FakeGitRunner(string? repoRoot) : IProcessRunner
    {
        public IReadOnlyList<string> DirtyFiles { get; init; } = [];

        public Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, Action<string>? onOutputLine = null, CancellationToken cancellationToken = default)
        {
            if (repoRoot is null)
            {
                return Task.FromResult(new ProcessResult(128, "", "fatal: not a git repository", TimeSpan.Zero));
            }

            var output = arguments[0] switch
            {
                "rev-parse" => repoRoot.Replace('\\', '/') + "\n",
                "symbolic-ref" => "main\n",
                "diff" => string.Concat(DirtyFiles.Concat(ModifiedLockfiles(workingDirectory)).Select(f => f + "\0")),
                _ => "",
            };
            return Task.FromResult(new ProcessResult(0, output, "", TimeSpan.Zero));
        }

        /// <summary>Like git: a lockfile is modified when its content differs from the committed "v1"
        /// (a trailing CR is ignored, as autocrlf would).</summary>
        private IEnumerable<string> ModifiedLockfiles(string projectDir)
        {
            var lockfile = Path.Combine(projectDir, "package-lock.json");
            if (File.Exists(lockfile) && File.ReadAllText(lockfile).TrimEnd('\r') != "v1")
            {
                yield return Path.GetRelativePath(repoRoot!, lockfile).Replace('\\', '/');
            }
        }
    }
}
