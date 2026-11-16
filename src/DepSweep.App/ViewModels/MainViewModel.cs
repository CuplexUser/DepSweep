using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepSweep.App.Services;
using DepSweep.Core.Config;
using DepSweep.Core.Models;
using DepSweep.Core.Reporting;
using DepSweep.Core.Sweep;

namespace DepSweep.App.ViewModels;

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
    }

    public ObservableCollection<RootViewModel> Roots { get; } = [];

    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];

    public ObservableCollection<IgnoredFolder> IgnoredFolders { get; } = [];

    public ObservableCollection<RepoCardViewModel> ReportRepos { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRootCommand))]
    public partial RootViewModel? SelectedRoot { get; set; }

    [ObservableProperty]
    public partial ProjectRowViewModel? SelectedProject { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscoverCommand), nameof(ScanCommand), nameof(FixCommand), nameof(CancelCommand), nameof(AddRootCommand), nameof(RemoveRootCommand))]
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
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand), nameof(ExportReportCommand))]
    public partial SweepReport? Report { get; set; }

    [ObservableProperty]
    public partial string ReportSummary { get; set; } = "Run Fix to produce a report of the repositories to commit.";

    public bool HasReport => Report is not null;

    public string SelectionSummary
    {
        get
        {
            var selected = Projects.Count(p => p.IsSelected);
            return $"{selected} of {Projects.Count} selected";
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
            StatusText = "Add a root folder to get started.";
        }
    }

    // ---------- Roots ----------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AddRootAsync()
    {
        var folder = _dialogs.PickFolder("Add a folder containing projects");
        if (folder is null)
        {
            return;
        }

        var existing = _config.FindRoot(folder);
        if (existing is null)
        {
            var model = new RootFolder { Path = PathUtil.Normalize(folder) };
            _config.Roots.Add(model);
            Roots.Add(new RootViewModel(model, SaveConfig));
            SaveConfig();
        }

        await DiscoverAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRoot))]
    private async Task RemoveRootAsync()
    {
        if (SelectedRoot is not { } root)
        {
            return;
        }

        _config.Roots.Remove(root.Model);
        Roots.Remove(root);
        SaveConfig();
        await DiscoverAsync();
    }

    private bool CanRemoveRoot() => IsIdle() && SelectedRoot is not null;

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
            Projects.Clear();
            foreach (var project in result.Projects)
            {
                var row = new ProjectRowViewModel(project, !_config.IsExcluded(project.Path), OnProjectSelectionChanged);
                if (previous.TryGetValue(project.Path, out var old))
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

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void SelectNone() => SetAllSelected(false);

    [RelayCommand]
    private void SelectVulnerable()
    {
        foreach (var p in Projects)
        {
            p.IsSelected = p.Counts is { Total: > 0 };
        }
    }

    private void SetAllSelected(bool value)
    {
        foreach (var p in Projects)
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
    private async Task ScanAsync()
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            StatusText = "No projects selected.";
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

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task FixAsync()
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            StatusText = "No projects selected.";
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
        }
    }

    private List<ProjectRowViewModel> SelectedRows() => Projects.Where(p => p.IsSelected).ToList();

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
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_reportStore.Directory}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private static void OpenProjectFolder(ProjectRowViewModel? row)
    {
        if (row is not null && Directory.Exists(row.Path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Path}\"") { UseShellExecute = true });
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
