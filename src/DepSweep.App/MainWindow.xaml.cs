using System.Windows;
using System.Windows.Controls;
using DepSweep.App.ViewModels;

namespace DepSweep.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
