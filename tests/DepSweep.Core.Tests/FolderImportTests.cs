using DepSweep.Core.Config;
using DepSweep.Core.Discovery;

namespace DepSweep.Core.Tests;

public sealed class FolderImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "depsweep-import-" + Guid.NewGuid().ToString("N"));

    public FolderImportTests() => Directory.CreateDirectory(_root);

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
            File.WriteAllText(Path.Combine(dir, f), f == "package.json" ? "{}" : "");
        }

        return dir;
    }

    [Fact]
    public void Project_folders_become_single_project_roots_and_other_folders_are_searched()
    {
        var project = Make("app", "package.json", "package-lock.json");
        var parent = Make("workspace");

        FolderImport.ToRoot(project).Recursive.ShouldBeFalse();
        FolderImport.ToRoot(parent).Recursive.ShouldBeTrue();
    }

    [Fact]
    public void AddRoots_skips_duplicates_and_missing_folders_and_maps_files_to_folders()
    {
        var a = Make("a");
        var b = Make("b", "package.json", "package-lock.json");
        var config = new DepSweepConfig { Roots = [new RootFolder { Path = a }], ExcludedProjects = [b] };

        var added = FolderImport.AddRoots(config, [
            new RootFolder { Path = a },
            new RootFolder { Path = Path.Combine(b, "package.json"), Recursive = false },
            new RootFolder { Path = Path.Combine(_root, "missing") },
        ]);

        added.Select(r => r.Path).ShouldBe([b]);
        added.Single().Recursive.ShouldBeFalse();
        config.Roots.Count.ShouldBe(2);
        config.IsExcluded(b).ShouldBeFalse("re-adding a project should include it again");
    }

    [Fact]
    public void Candidates_are_flagged_when_an_existing_root_covers_them()
    {
        Make("repos/one", "package.json", "package-lock.json");
        Make("repos/two", "package.json", "pnpm-lock.yaml");
        Directory.CreateDirectory(Path.Combine(_root, "repos", "two", ".git"));
        File.WriteAllText(Path.Combine(_root, "repos", "two", ".git", "HEAD"), "ref: refs/heads/develop\n");
        var config = new DepSweepConfig { Roots = [new RootFolder { Path = Path.Combine(_root, "repos", "one"), Recursive = false }] };

        var candidates = FolderImport.FindCandidates(config, [Path.Combine(_root, "repos")]);

        candidates.Count.ShouldBe(2);
        candidates.Single(c => c.Project.Name == "one").AlreadyIncluded.ShouldBeTrue();
        var two = candidates.Single(c => c.Project.Name == "two");
        two.AlreadyIncluded.ShouldBeFalse();
        two.RepoRoot.ShouldBe(Path.Combine(_root, "repos", "two"));
        GitRepoLocator.ReadBranch(two.RepoRoot!).ShouldBe("develop");
    }

    [Fact]
    public void Disabled_roots_do_not_cover_projects()
    {
        var p = Make("x", "package.json", "package-lock.json");
        var config = new DepSweepConfig { Roots = [new RootFolder { Path = _root, Enabled = false }] };
        FolderImport.IsCovered(config, p).ShouldBeFalse();
        config.Roots[0].Enabled = true;
        FolderImport.IsCovered(config, p).ShouldBeTrue();
    }

    [Fact]
    public void Worktree_git_file_and_detached_head_are_handled()
    {
        var repo = Make("wt");
        var gitDir = Make("real-git-dir");
        File.WriteAllText(Path.Combine(repo, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a\n");

        GitRepoLocator.FindRepoRoot(Path.Combine(repo)).ShouldBe(repo);
        GitRepoLocator.ReadBranch(repo).ShouldBe("(detached)");
    }
}
