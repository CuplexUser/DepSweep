using DepSweep.Core.Config;
using DepSweep.Core.Discovery;
using DepSweep.Core.Models;

namespace DepSweep.Core.Tests;

public sealed class DiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "depsweep-tests-" + Guid.NewGuid().ToString("N"));

    public DiscoveryTests() => Directory.CreateDirectory(_root);

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

    private string Make(string relative, params string[] files)
    {
        var dir = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(dir);
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(dir, f), f == "package.json" ? $$"""{ "name": "{{Path.GetFileName(dir)}}-pkg" }""" : "");
        }

        return dir;
    }

    [Fact]
    public void Finds_npm_and_pnpm_projects_and_skips_node_modules_and_workspace_members()
    {
        var npm = Make("repo-a/web", "package.json", "package-lock.json");
        Make("repo-a/web/node_modules/lodash", "package.json", "package-lock.json");
        var pnpm = Make("repo-b", "package.json", "pnpm-lock.yaml");
        Make("repo-b/packages/ui", "package.json"); // workspace member: covered by the root lockfile
        Make("repo-c/.hidden", "package.json", "package-lock.json");
        var noLock = Make("repo-d", "package.json");
        var yarn = Make("repo-e", "package.json", "yarn.lock");

        var result = new ProjectDiscovery().Discover([new RootFolder { Path = _root }]);

        result.Projects.Select(p => p.Path).ShouldBe([npm, pnpm], ignoreOrder: true);
        result.Projects.Single(p => p.Path == npm).PackageManager.ShouldBe(PackageManagerKind.Npm);
        result.Projects.Single(p => p.Path == npm).Name.ShouldBe("web-pkg");
        result.Projects.Single(p => p.Path == npm).RelativePath.ShouldBe("repo-a/web");
        result.Projects.Single(p => p.Path == pnpm).PackageManager.ShouldBe(PackageManagerKind.Pnpm);

        result.Ignored.Select(i => i.Path).ShouldBe([noLock, yarn], ignoreOrder: true);
        result.Ignored.Single(i => i.Path == yarn).Reason.ShouldContain("yarn");
    }

    [Fact]
    public void Non_recursive_root_only_checks_the_folder_itself()
    {
        Make("child", "package.json", "package-lock.json");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "package-lock.json"), "{}");

        var result = new ProjectDiscovery().Discover([new RootFolder { Path = _root, Recursive = false }]);

        result.Projects.Single().Path.ShouldBe(_root);
        result.Projects.Single().Name.ShouldBe(Path.GetFileName(_root), "falls back to folder name when package.json has no name");
    }

    [Fact]
    public void Overlapping_roots_do_not_duplicate_projects_and_missing_roots_are_reported()
    {
        Make("a", "package.json", "package-lock.json");
        var missing = Path.Combine(_root, "does-not-exist");

        var result = new ProjectDiscovery().Discover([
            new RootFolder { Path = _root },
            new RootFolder { Path = Path.Combine(_root, "a") },
            new RootFolder { Path = missing },
            new RootFolder { Path = Path.Combine(_root, "a"), Enabled = false },
        ]);

        result.Projects.Count.ShouldBe(1);
        result.Ignored.ShouldContain(i => i.Path == missing);
    }

    [Fact]
    public void Both_lockfiles_prefers_packageManager_field()
    {
        var dir = Make("both", "package-lock.json", "pnpm-lock.yaml");
        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "both", "packageManager": "npm@10.0.0" }""");

        ProjectDiscovery.DetectPackageManager(dir, out _).ShouldBe(PackageManagerKind.Npm);

        File.WriteAllText(Path.Combine(dir, "package.json"), """{ "name": "both" }""");
        ProjectDiscovery.DetectPackageManager(dir, out _).ShouldBe(PackageManagerKind.Pnpm);
    }
}
