using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DepSweep.Core.Config;
using DepSweep.Core.Models;

namespace DepSweep.App.ViewModels;

public sealed partial class ProjectRowViewModel : ObservableObject
{
    private const int MaxLogLines = 400;
    private readonly Queue<string> _log = new();
    private readonly Action<ProjectRowViewModel> _onSelectionChanged;

    public ProjectRowViewModel(NodeProject project, bool isSelected, Action<ProjectRowViewModel> onSelectionChanged)
    {
        Project = project;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public NodeProject Project { get; }

    public string Name => Project.Name;

    public string Path => Project.Path;

    public string RelativePath => Project.RelativePath;

    public string Manager => Project.PackageManager.ToString().ToLowerInvariant();

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged(this);
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsBusy))]
    public partial ProjectStatus Status { get; set; } = ProjectStatus.NotScanned;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Critical), nameof(High), nameof(Moderate), nameof(Low), nameof(HasCounts))]
    public partial SeverityCounts? Counts { get; set; }

    [ObservableProperty]
    public partial string? Repo { get; set; }

    [ObservableProperty]
    public partial string? Branch { get; set; }

    [ObservableProperty]
    public partial string LogText { get; set; } = "";

    [ObservableProperty]
    public partial ProjectResult? LastResult { get; set; }

    public ObservableCollection<Vulnerability> Vulnerabilities { get; } = [];

    public int? Critical => Counts?.Critical;
    public int? High => Counts?.High;
    public int? Moderate => Counts?.Moderate;
    public int? Low => Counts?.Low;
    public bool HasCounts => Counts is not null;

    public bool IsBusy => Status is ProjectStatus.Queued or ProjectStatus.Scanning or ProjectStatus.Fixing;

    public string StatusText => Status switch
    {
        ProjectStatus.NotScanned => "Not scanned",
        ProjectStatus.WouldFix => "Would fix",
        _ => Status.ToString(),
    };

    public void ApplyResult(ProjectResult result)
    {
        LastResult = result;
        Status = result.Status;
        Message = result.Message;
        if (result.Current is { } audit)
        {
            Counts = audit.Counts;
            Vulnerabilities.Clear();
            foreach (var v in audit.Vulnerabilities)
            {
                Vulnerabilities.Add(v);
            }
        }

        if (result.Git is { } git)
        {
            Repo = git.RepoRoot;
            Branch = git.Branch;
        }

        if (result.ChangedFiles.Count > 0)
        {
            AppendLog("Changed: " + string.Join(", ", result.ChangedFiles.Select(f => PathUtil.ToRelativeForwardSlash(Path, f))));
        }
    }

    /// <summary>Carries results over when the project list is rebuilt by a new discovery.</summary>
    public void CopyStateFrom(ProjectRowViewModel other)
    {
        if (other.LastResult is { } result)
        {
            ApplyResult(result);
        }

        foreach (var line in other._log)
        {
            AppendLog(line);
        }
    }

    public void AppendLog(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines)
        {
            _log.Dequeue();
        }

        var sb = new StringBuilder();
        foreach (var l in _log)
        {
            sb.AppendLine(l);
        }

        LogText = sb.ToString();
    }
}
