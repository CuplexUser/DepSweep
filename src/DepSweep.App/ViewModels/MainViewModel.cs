using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepSweep.App.Services;
using DepSweep.Core;
using DepSweep.Core.Config;
using DepSweep.Core.Discovery;
using DepSweep.Core.Models;
using DepSweep.Core.Reporting;
using DepSweep.Core.Sweep;

namespace DepSweep.App.ViewModels;

public enum ProjectFilter
{
    All,
    Vulnerable,
    Changed,
    Problems,
    NotScanned,
    Clean,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConfigStore _configStore;
    private readonly ReportStore _reportStore;
    private readonly SweepService _sweep;
    private readonly IDialogService _dialogs;
    private DepSweepConfig _config;
    private CancellationTokenSource? _cts;

    public MainViewModel(ConfigStore configStore, ReportStore reportStore, SweepService sweep, IDialogService dialogs)
    {
        _configStore = configStore;
        _reportStore = reportStore;
        _sweep = sweep;
        _dialogs = dialogs;
        _config = LoadConfig();
        foreach (var root in _config.Roots)
        {
            Roots.Add(new RootViewModel(root, SaveConfig));
        }

        ProjectsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Projects);
        ProjectsView.Filter = o => o is ProjectRowViewModel row && MatchesFilter(row);
        ProjectsView.IsLiveFiltering = true;
        ProjectsView.LiveFilteringProperties.Add(nameof(ProjectRowViewModel.Status));
        ProjectsView.IsLiveGrouping = true;
        ProjectsView.LiveGroupingProperties.Add(nameof(ProjectRowViewModel.RepoGroupName));
        ApplyGrouping();
    }

    public string WindowTitle => $"DepSweep {AppInfo.Version}";

    public string VersionText => $"v{AppInfo.DisplayVersion}";

    public ObservableCollection<RootViewModel> Roots { get; } = [];

    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];

    /// <summary>Filtered/grouped view of <see cref="Projects"/> shown in the grid.</summary>
    public ListCollectionView ProjectsView { get; }

    public ObservableCollection<IgnoredFolder> IgnoredFolders { get; } = [];

    public ObservableCollection<RepoCardViewModel> ReportRepos { get; } = [];

    public IReadOnlyList<ProjectFilter> FilterOptions { get; } = Enum.GetValues<ProjectFilter>();

    [ObservableProperty]
    public partial RootViewModel? SelectedRoot { get; set; }

    [ObservableProperty]
    public partial ProjectRowViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(DiscoverCommand), nameof(ScanCommand), nameof(FixCommand), nameof(CancelCommand),
        nameof(AddFoldersCommand), nameof(ImportProjectsCommand), nameof(RemoveRootsCommand),
        nameof(ScanRowsCommand), nameof(FixRowsCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool Force { get; set; }

    [ObservableProperty]
    public partial bool AllowDirty { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial ProjectFilter StatusFilter { get; set; } = ProjectFilter.All;

    [ObservableProperty]
    public partial bool GroupByRepo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand), nameof(ExportReportCommand))]
    public partial SweepReport? Report { get; set; }

    [ObservableProperty]
    public partial string ReportSummary { get; set; } = "Run Fix to produce a report of the repositories to commit.";

    public bool HasReport => Report is not null;

    public bool HasRoots => Roots.Count > 0;

    public string SelectionSummary
    {
        get
        {
            var visible = ProjectsView.Count;
            var text = $"{Projects.Count(p => p.IsSelected)} of {Projects.Count} checked";
            return visible == Projects.Count ? text : $"{text} · {visible} shown";
        }
    }

    public async Task InitializeAsync()
    {
        if (Roots.Count > 0)
        {
            await DiscoverAsync();
        }
        else
        {
            StatusText = "Add folders or import projects to get started (you can also drop folders on the window).";
        }
    }

    // ---------- Filtering / grouping ----------

    partial void OnFilterTextChanged(string value) => RefreshView();

    partial void OnStatusFilterChanged(ProjectFilter value) => RefreshView();

    partial void OnGroupByRepoChanged(bool value) => ApplyGrouping();

    private void ApplyGrouping()
    {
        ProjectsView.GroupDescriptions.Clear();
        if (GroupByRepo)
        {
            ProjectsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProjectRowViewModel.RepoGroupName)));
        }
    }

    private void RefreshView()
    {
        ProjectsView.Refresh();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private bool MatchesFilter(ProjectRowViewModel row)
    {
        if (FilterText.Length > 0
            && !row.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            && !row.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            && !(row.Branch?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        return StatusFilter switch
        {
            ProjectFilter.Vulnerable => row.Counts is { Total: > 0 },
            ProjectFilter.Changed => row.HasChanges,
            ProjectFilter.Problems => row.HasProblem,
            ProjectFilter.NotScanned => row.Status == ProjectStatus.NotScanned,
            ProjectFilter.Clean => row.Status is ProjectStatus.Clean or ProjectStatus.Fixed,
            _ => true,
        };
    }

    // ---------- Roots ----------

    /// <summary>Multi-select folder picker. Folders that are projects are added as projects, others as search folders.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AddFoldersAsync()
    {
        var folders = _dialogs.PickFolders("Add folders (select one or more)");
        if (folders.Count > 0)
        {
            await AddRootsAsync(folders.Select(FolderImport.ToRoot));
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportProjectsAsync()
    {
        var import = new ImportProjectsViewModel(_config, _dialogs);
        if (_dialogs.ShowImport(import) && import.Result.Count > 0)
        {
            await AddRootsAsync(import.Result);
        }
    }

    /// <summary>Folders or files dropped on the window.</summary>
    public async Task AddDroppedAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0)
        {
            return;
        }

        // A drop of many folders that are not projects themselves is best reviewed in the import dialog.
        var roots = paths.Select(FolderImport.ToRoot).ToList();
        if (roots.Count > 1 && roots.All(r => r.Recursive))
        {
            var import = new ImportProjectsViewModel(_config, _dialogs, paths);
            if (_dialogs.ShowImport(import) && import.Result.Count > 0)
            {
                await AddRootsAsync(import.Result);
            }

            return;
        }

        await AddRootsAsync(roots);
    }

    private async Task AddRootsAsync(IEnumerable<RootFolder> roots)
    {
        var added = FolderImport.AddRoots(_config, roots);
        foreach (var root in added)
        {
            Roots.Add(new RootViewModel(root, SaveConfig));
        }

        OnPropertyChanged(nameof(HasRoots));
        if (added.Count == 0)
        {
            StatusText = "Nothing new to add (already configured or not found).";
            return;
        }

        SaveConfig();
        await DiscoverAsync();
        StatusText = $"Added {added.Count} folder(s). " + StatusText;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RemoveRootsAsync(IList? selected)
    {
        var roots = (selected?.OfType<RootViewModel>() ?? (SelectedRoot is null ? [] : [SelectedRoot])).ToList();
        if (roots.Count == 0)
        {
            return;
        }

        if (roots.Count > 1 && !_dialogs.Confirm("Remove folders", $"Remove {roots.Count} folders from DepSweep?\n\nNothing is deleted from disk."))
        {
            return;
        }

        foreach (var root in roots)
        {
            _config.Roots.Remove(root.Model);
            Roots.Remove(root);
        }

        OnPropertyChanged(nameof(HasRoots));
        SaveConfig();
        await DiscoverAsync();
    }

    [RelayCommand]
    private void EnableRoots(IList? selected) => SetRoots(selected, r => r.Enabled = true);

    [RelayCommand]
    private void DisableRoots(IList? selected) => SetRoots(selected, r => r.Enabled = false);

    [RelayCommand]
    private void SearchSubfolders(IList? selected) => SetRoots(selected, r => r.Recursive = true);

    [RelayCommand]
    private void ThisFolderOnly(IList? selected) => SetRoots(selected, r => r.Recursive = false);

    [RelayCommand]
    private static void OpenRootFolder(IList? selected)
    {
        foreach (var root in selected?.OfType<RootViewModel>().Take(5) ?? [])
        {
            OpenInExplorer(root.Path);
        }
    }

    private void SetRoots(IList? selected, Action<RootViewModel> change)
    {
        foreach (var root in selected?.OfType<RootViewModel>() ?? [])
        {
            change(root);
        }

        StatusText = "Folder settings changed; click Discover to refresh the project list.";
    }

    // ---------- Discovery ----------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task DiscoverAsync()
    {
        IsBusy = true;
        StatusText = "Searching for projects…";
        try
        {
            var config = _config;
            var result = await Task.Run(() => SweepService.Discover(config));

            var previous = Projects.ToDictionary(p => p.Path, PathUtil.Comparer);
            var rows = await Task.Run(() => result.Projects
                .Select(project => new ProjectRowViewModel(project, !config.IsExcluded(project.Path), OnProjectSelectionChanged))
                .ToList());

            SelectedProject = null;
            Projects.Clear();
            foreach (var row in rows)
            {
                if (previous.TryGetValue(row.Path, out var old))
                {
                    row.CopyStateFrom(old);
                }

                Projects.Add(row);
            }

            IgnoredFolders.Clear();
            foreach (var ignored in result.Ignored)
            {
                IgnoredFolders.Add(ignored);
            }

            OnPropertyChanged(nameof(SelectionSummary));
            StatusText = $"Found {result.Projects.Count} project(s)" + (result.Ignored.Count > 0 ? $", {result.Ignored.Count} folder(s) skipped" : "");
        }
        catch (Exception ex)
        {
            StatusText = "Discovery failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- Checking rows ----------

    [RelayCommand]
    private void SelectAll() => SetChecked(ProjectsView.Cast<ProjectRowViewModel>(), true);

    [RelayCommand]
    private void SelectNone() => SetChecked(ProjectsView.Cast<ProjectRowViewModel>(), false);

    [RelayCommand]
    private void SelectVulnerable()
    {
        foreach (var p in Projects)
        {
            p.IsSelected = p.Counts is { Total: > 0 };
        }
    }

    [RelayCommand]
    private void CheckRows(IList? rows) => SetChecked(Rows(rows), true);

    [RelayCommand]
    private void UncheckRows(IList? rows) => SetChecked(Rows(rows), false);

    /// <summary>Space bar: toggles the highlighted rows (checks all unless all are already checked).</summary>
    public void ToggleRows(IList rows)
    {
        var list = Rows(rows);
        SetChecked(list, !list.All(r => r.IsSelected));
    }

    private static List<ProjectRowViewModel> Rows(IList? rows) => rows?.OfType<ProjectRowViewModel>().ToList() ?? [];

    private void SetChecked(IEnumerable<ProjectRowViewModel> rows, bool value)
    {
        foreach (var p in rows.ToList())
        {
            p.IsSelected = value;
        }
    }

    private void OnProjectSelectionChanged(ProjectRowViewModel row)
    {
        _config.SetExcluded(row.Path, !row.IsSelected);
        SaveConfig();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    // ---------- Scan / Fix ----------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ScanAsync() => ScanRowsCoreAsync(CheckedRows());

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task FixAsync() => FixRowsCoreAsync(CheckedRows());

    [RelayCommand(CanExecute = nameof(CanRunRows))]
    private Task ScanRowsAsync(IList? rows) => ScanRowsCoreAsync(Rows(rows));

    [RelayCommand(CanExecute = nameof(CanRunRows))]
    private Task FixRowsAsync(IList? rows) => FixRowsCoreAsync(Rows(rows));

    private bool CanRunRows(IList? rows) => !IsBusy;

    private async Task ScanRowsCoreAsync(IReadOnlyList<ProjectRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            StatusText = "No projects checked.";
            return;
        }

        await RunAsync(rows, "Scanning", async (projects, options, progress, ct) =>
        {
            var results = await _sweep.ScanAsync(projects, options, progress, ct);
            var vulnerable = results.Count(r => r.Status == ProjectStatus.Vulnerable);
            var total = results.Aggregate(SeverityCounts.Empty, (acc, r) => acc + (r.Current?.Counts ?? SeverityCounts.Empty));
            StatusText = $"Scanned {results.Count} project(s): {vulnerable} vulnerable ({total.ToSummary()}), {results.Count(r => r.Status == ProjectStatus.Error)} error(s)";
        });
    }

    private async Task FixRowsCoreAsync(IReadOnlyList<ProjectRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            StatusText = "No projects checked.";
            return;
        }

        var message = $"Run audit fix on {rows.Count} project(s)?\n\nThis updates package.json / lockfiles. Nothing is committed or pushed.";
        if (Force)
        {
            message += "\n\n--force is ON: breaking (semver-major) upgrades may be applied.";
        }

        if (!_dialogs.Confirm("Fix vulnerabilities", message, warning: Force))
        {
            return;
        }

        await RunAsync(rows, Force ? "Fixing (--force)" : "Fixing", async (projects, options, progress, ct) =>
        {
            var report = await _sweep.FixAsync(projects, options with { Force = Force, AllowDirty = AllowDirty }, progress, ct);
            ShowReport(report);
            try
            {
                _reportStore.Save(report);
            }
            catch (IOException ex)
            {
                StatusText = "Could not save report: " + ex.Message;
            }

            var changed = report.ChangedRepositories.Count();
            StatusText = $"{(report.Cancelled ? "Cancelled. " : "")}{changed} repositor{(changed == 1 ? "y" : "ies")} with changes · remaining {report.TotalRemaining.ToSummary()}";
            SelectedTab = 1;
        });
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
    }

    private async Task RunAsync(
        IReadOnlyList<ProjectRowViewModel> rows,
        string verb,
        Func<IReadOnlyList<NodeProject>, SweepOptions, IProgress<SweepEvent>, CancellationToken, Task> action)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"{verb} {rows.Count} project(s)…";

        var byPath = rows.ToDictionary(r => r.Path, PathUtil.Comparer);
        var progress = new Progress<SweepEvent>(e =>
        {
            if (!byPath.TryGetValue(e.Project.Path, out var row))
            {
                return;
            }

            if (e.IsOutputLine)
            {
                row.AppendLog(e.Message ?? "");
                return;
            }

            if (e.Result is { } result)
            {
                row.ApplyResult(result);
                var done = rows.Count(r => !r.IsBusy);
                StatusText = $"{verb}… {done}/{rows.Count}";
            }
            else
            {
                row.Status = e.Status;
                if (e.Message is not null)
                {
                    row.Message = e.Message;
                    row.AppendLog($"[{DateTime.Now:HH:mm:ss}] {e.Message}");
                }
            }
        });

        try
        {
            await action(rows.Select(r => r.Project).ToList(), SweepOptions.FromConfig(_config), progress, _cts.Token);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("DepSweep", ex.Message);
            StatusText = "Failed: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            RefreshView();
        }
    }

    private List<ProjectRowViewModel> CheckedRows() => Projects.Where(p => p.IsSelected).ToList();

    private bool IsIdle() => !IsBusy;

    // ---------- Report ----------

    private void ShowReport(SweepReport report)
    {
        Report = report;
        ReportRepos.Clear();
        foreach (var repo in report.Repositories)
        {
            ReportRepos.Add(new RepoCardViewModel(repo));
        }

        var changed = report.ChangedRepositories.Count();
        ReportSummary = $"{report.StartedAt:yyyy-MM-dd HH:mm} · {report.Projects.Count} project(s){(report.Force ? " · --force" : "")}"
            + $" · before {report.TotalBefore.ToSummary()} · remaining {report.TotalRemaining.ToSummary()}"
            + $" · {changed} repositor{(changed == 1 ? "y" : "ies")} to commit";
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyReport()
    {
        if (Report is not null)
        {
            Clipboard.SetText(MarkdownReportWriter.Write(Report));
            StatusText = "Report copied to clipboard as Markdown.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void ExportReport()
    {
        if (Report is null)
        {
            return;
        }

        var path = _dialogs.PickSaveFile(
            $"depsweep-{Report.StartedAt:yyyyMMdd-HHmmss}.md",
            "Markdown (*.md)|*.md|JSON (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        var content = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonReportWriter.Write(Report)
            : MarkdownReportWriter.Write(Report);
        File.WriteAllText(path, content);
        StatusText = $"Report exported to {path}";
    }

    [RelayCommand]
    private void OpenReportsFolder()
    {
        Directory.CreateDirectory(_reportStore.Directory);
        OpenInExplorer(_reportStore.Directory);
    }

    [RelayCommand]
    private static void OpenProjectFolder(IList? rows)
    {
        foreach (var row in Rows(rows).Take(5))
        {
            OpenInExplorer(row.Path);
        }
    }

    [RelayCommand]
    private static void CopyProjectPaths(IList? rows)
    {
        var paths = Rows(rows).Select(r => r.Path).ToList();
        if (paths.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
    }

    private static void OpenInExplorer(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    // ---------- Config ----------

    private DepSweepConfig LoadConfig()
    {
        try
        {
            return _configStore.Load();
        }
        catch (InvalidOperationException ex)
        {
            _dialogs.ShowError("DepSweep", ex.Message + "\n\nStarting with an empty configuration.");
            return new DepSweepConfig();
        }
    }

    private void SaveConfig()
    {
        try
        {
            _configStore.Save(_config);
        }
        catch (IOException ex)
        {
            StatusText = "Could not save settings: " + ex.Message;
        }
    }
}
