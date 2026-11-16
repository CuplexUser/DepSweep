using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepSweep.App.Services;
using DepSweep.Core.Config;
using DepSweep.Core.Discovery;

namespace DepSweep.App.ViewModels;

public sealed partial class ImportCandidateViewModel(ImportCandidate candidate, string sourceFolder) : ObservableObject
{
    public ImportCandidate Candidate { get; } = candidate;

    public string Name => Candidate.Project.Name;

    public string Path => Candidate.Project.Path;

    public string RelativePath => PathUtil.ToRelativeForwardSlash(sourceFolder, Candidate.Project.Path);

    public string Manager => Candidate.Project.PackageManager.ToString().ToLowerInvariant();

    public string Repo => Candidate.RepoRoot is { } r ? System.IO.Path.GetFileName(r) : "(no git)";

    public bool AlreadyIncluded => Candidate.AlreadyIncluded;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = !candidate.AlreadyIncluded;
}

public enum ImportMode
{
    /// <summary>Add each checked project as its own (non-recursive) entry.</summary>
    Projects,

    /// <summary>Add the source folders as recursive search roots.</summary>
    SearchRoots,
}

/// <summary>Backs the "Import projects" dialog: pick several parent folders, review found projects, add many at once.</summary>
public sealed partial class ImportProjectsViewModel : ObservableObject
{
    private readonly DepSweepConfig _config;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public ImportProjectsViewModel(DepSweepConfig config, IDialogService dialogs, IEnumerable<string>? initialFolders = null)
    {
        _config = config;
        _dialogs = dialogs;
        CandidatesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Candidates);
        CandidatesView.Filter = o => o is ImportCandidateViewModel c && Matches(c);
        foreach (var f in initialFolders ?? [])
        {
            AddSource(f);
        }
    }

    public ObservableCollection<string> SourceFolders { get; } = [];

    public ObservableCollection<ImportCandidateViewModel> Candidates { get; } = [];

    public ListCollectionView CandidatesView { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSourceCommand))]
    public partial string? SelectedSource { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial bool HideIncluded { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectsMode), nameof(AddButtonText))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    public partial ImportMode Mode { get; set; } = ImportMode.Projects;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Add one or more folders to search for projects.";

    /// <summary>Set when the dialog is accepted: the roots to add.</summary>
    public IReadOnlyList<RootFolder> Result { get; private set; } = [];

    /// <summary>Raised to close the dialog (true = accepted).</summary>
    public event Action<bool>? CloseRequested;

    public bool IsProjectsMode
    {
        get => Mode == ImportMode.Projects;
        set => Mode = value ? ImportMode.Projects : ImportMode.SearchRoots;
    }

    public int CheckedCount => Candidates.Count(c => c.IsChecked);

    public string AddButtonText => Mode == ImportMode.Projects
        ? $"Add {CheckedCount} project{(CheckedCount == 1 ? "" : "s")}"
        : $"Add {SourceFolders.Count} folder{(SourceFolders.Count == 1 ? "" : "s")}";

    partial void OnFilterTextChanged(string value) => CandidatesView.Refresh();

    partial void OnHideIncludedChanged(bool value) => CandidatesView.Refresh();

    private bool Matches(ImportCandidateViewModel c) =>
        (!HideIncluded || !c.AlreadyIncluded)
        && (FilterText.Length == 0
            || c.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || c.Path.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    // ---------- Sources ----------

    [RelayCommand]
    private async Task AddSourcesAsync()
    {
        var folders = _dialogs.PickFolders("Choose folders to search for projects");
        if (folders.Count == 0)
        {
            return;
        }

        foreach (var f in folders)
        {
            AddSource(f);
        }

        await SearchAsync();
    }

    [RelayCommand]
    private async Task PasteSourcesAsync()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        var paths = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"'))
            .Where(p => p.Length > 0)
            .ToList();
        var added = paths.Count(AddSource);
        StatusText = added > 0
            ? $"Added {added} folder(s) from the clipboard."
            : "The clipboard has no existing folder paths (one path per line).";
        if (added > 0)
        {
            await SearchAsync();
        }
    }

    public async Task AddDroppedAsync(IEnumerable<string> paths)
    {
        if (paths.Count(AddSource) > 0)
        {
            await SearchAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSource))]
    private async Task RemoveSourceAsync()
    {
        if (SelectedSource is { } s)
        {
            SourceFolders.Remove(s);
            OnPropertyChanged(nameof(AddButtonText));
            await SearchAsync();
        }
    }

    private bool HasSelectedSource() => SelectedSource is not null;

    private bool AddSource(string path)
    {
        try
        {
            var folder = File.Exists(path) ? System.IO.Path.GetDirectoryName(path)! : path;
            if (!Directory.Exists(folder))
            {
                return false;
            }

            folder = PathUtil.Normalize(folder);
            if (SourceFolders.Any(s => PathUtil.Equal(s, folder)))
            {
                return false;
            }

            SourceFolders.Add(folder);
            OnPropertyChanged(nameof(AddButtonText));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    // ---------- Search ----------

    [RelayCommand]
    public async Task SearchAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var sources = SourceFolders.ToList();
        Candidates.Clear();
        UpdateCounts();
        if (sources.Count == 0)
        {
            StatusText = "Add one or more folders to search for projects.";
            return;
        }

        IsSearching = true;
        StatusText = "Searching…";
        try
        {
            var found = await Task.Run(() => FolderImport.FindCandidates(_config, sources, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var c in found)
            {
                var source = sources.FirstOrDefault(s => PathUtil.IsUnder(c.Project.Path, s)) ?? c.Project.RootPath;
                var vm = new ImportCandidateViewModel(c, source);
                vm.PropertyChanged += OnCandidateChanged;
                Candidates.Add(vm);
            }

            var included = found.Count(c => c.AlreadyIncluded);
            StatusText = $"Found {found.Count} project(s)" + (included > 0 ? $", {included} already included" : "") + ".";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_cts == cts)
            {
                IsSearching = false;
            }

            UpdateCounts();
        }
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportCandidateViewModel.IsChecked))
        {
            UpdateCounts();
        }
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(AddButtonText));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CheckVisible() => SetVisible(true);

    [RelayCommand]
    private void UncheckVisible() => SetVisible(false);

    private void SetVisible(bool value)
    {
        foreach (var c in CandidatesView.Cast<ImportCandidateViewModel>())
        {
            c.IsChecked = value;
        }
    }

    // ---------- Accept ----------

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept()
    {
        Result = Mode == ImportMode.Projects
            ? Candidates.Where(c => c.IsChecked).Select(c => new RootFolder { Path = c.Path, Recursive = false }).ToList()
            : SourceFolders.Select(s => new RootFolder { Path = s, Recursive = true }).ToList();
        CloseRequested?.Invoke(true);
    }

    private bool CanAccept() => !IsSearching && (Mode == ImportMode.Projects ? CheckedCount > 0 : SourceFolders.Count > 0);

    [RelayCommand]
    private void CancelDialog()
    {
        _cts?.Cancel();
        CloseRequested?.Invoke(false);
    }
}
