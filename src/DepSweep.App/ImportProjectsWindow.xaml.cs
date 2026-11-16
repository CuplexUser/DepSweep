using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DepSweep.App.ViewModels;

namespace DepSweep.App;

public partial class ImportProjectsWindow : Window
{
    private readonly ImportProjectsViewModel _viewModel;

    public ImportProjectsWindow(ImportProjectsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += accepted =>
        {
            DialogResult = accepted;
        };
        Loaded += async (_, _) =>
        {
            if (viewModel.SourceFolders.Count > 0)
            {
                await viewModel.SearchAsync();
            }
        };
    }

    private void Window_DragOver(object sender, DragEventArgs e) => DropHelper.SetEffects(e);

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (DropHelper.GetPaths(e) is { Count: > 0 } paths)
        {
            await _viewModel.AddDroppedAsync(paths);
        }
    }

    /// <summary>Space toggles the check box of every highlighted row.</summary>
    private void CandidatesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not DataGrid grid || grid.SelectedItems.Count == 0)
        {
            return;
        }

        var rows = grid.SelectedItems.OfType<ImportCandidateViewModel>().ToList();
        var check = !rows.All(r => r.IsChecked);
        foreach (var row in rows)
        {
            row.IsChecked = check;
        }

        e.Handled = true;
    }
}

internal static class DropHelper
{
    public static void SetEffects(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    public static IReadOnlyList<string> GetPaths(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];
}
