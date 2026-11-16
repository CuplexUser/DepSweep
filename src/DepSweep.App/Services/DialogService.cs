using System.Windows;
using Microsoft.Win32;

namespace DepSweep.App.Services;

public interface IDialogService
{
    string? PickFolder(string title);

    string? PickSaveFile(string defaultName, string filter);

    bool Confirm(string title, string message, bool warning = false);

    void ShowError(string title, string message);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string defaultName, string filter)
    {
        var dialog = new SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message, bool warning = false) =>
        Show(message, title, MessageBoxButton.OKCancel, warning ? MessageBoxImage.Warning : MessageBoxImage.Question)
            == MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { IsLoaded: true } owner
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
}
