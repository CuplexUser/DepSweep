using CommunityToolkit.Mvvm.ComponentModel;
using DepSweep.Core.Config;

namespace DepSweep.App.ViewModels;

public sealed partial class RootViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public RootViewModel(RootFolder model, Action onChanged)
    {
        Model = model;
        _onChanged = onChanged;
    }

    public RootFolder Model { get; }

    public string Path => Model.Path;

    public string DisplayName => System.IO.Path.GetFileName(Model.Path) is { Length: > 0 } name ? name : Model.Path;

    public bool Exists => System.IO.Directory.Exists(Model.Path);

    /// <summary>"Search folder" for recursive roots, "Project" for single-folder roots.</summary>
    public string Kind => Model.Recursive ? "Search folder" : "Project";

    public bool Recursive
    {
        get => Model.Recursive;
        set
        {
            if (Model.Recursive != value)
            {
                Model.Recursive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Kind));
                _onChanged();
            }
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled != value)
            {
                Model.Enabled = value;
                OnPropertyChanged();
                _onChanged();
            }
        }
    }
}
