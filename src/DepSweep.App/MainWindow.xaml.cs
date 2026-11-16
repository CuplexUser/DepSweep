using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DepSweep.App.ViewModels;

namespace DepSweep.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // DataGrid star columns collapse when rows arrive while the grid is empty or grouped;
        // re-apply the star widths once layout has settled.
        viewModel.Projects.CollectionChanged += (_, _) => ScheduleColumnRefresh();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.GroupByRepo))
            {
                ScheduleColumnRefresh();
            }
        };
    }

    private bool _columnRefreshPending;

    private void ScheduleColumnRefresh()
    {
        if (_columnRefreshPending)
        {
            return;
        }

        _columnRefreshPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _columnRefreshPending = false;
            foreach (var column in ProjectsGrid.Columns.Where(c => c.Width.IsStar))
            {
                var width = column.Width;
                column.Width = 0;
                column.Width = width;
            }
        });
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();

    private void Window_DragOver(object sender, DragEventArgs e) => DropHelper.SetEffects(e);

    private async void Window_Drop(object sender, DragEventArgs e) => await _viewModel.AddDroppedAsync(DropHelper.GetPaths(e));

    /// <summary>Space toggles the check box of every highlighted project row.</summary>
    private void ProjectsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && sender is DataGrid { SelectedItems.Count: > 0 } grid
            && e.OriginalSource is not CheckBox)
        {
            _viewModel.ToggleRows(grid.SelectedItems);
            e.Handled = true;
        }
    }

    /// <summary>Delete removes the highlighted folders.</summary>
    private void RootsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _viewModel.RemoveRootsCommand.CanExecute(RootsList.SelectedItems))
        {
            _viewModel.RemoveRootsCommand.Execute(RootsList.SelectedItems);
            e.Handled = true;
        }
    }
}
