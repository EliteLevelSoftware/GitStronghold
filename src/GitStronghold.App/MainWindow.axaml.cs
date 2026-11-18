using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using GitStronghold.App.ViewModels;

namespace GitStronghold.App;

public partial class MainWindow : EliteLevel.Avalonia.Controls.EliteWindow
{
    public MainWindow()
    {
        InitializeComponent();
        var model = new MainViewModel();
        model.PickFolder = PickFolderAsync;
        DataContext = model;
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Backup folder"
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e) => DragMove(e);

    private void OnMinimize(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnResizeTop(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.North, e);

    private void OnResizeBottom(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.South, e);

    private void OnResizeLeft(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.West, e);

    private void OnResizeRight(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.East, e);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void OnMarkdownNavigate(object? sender, string path)
    {
        if (DataContext is MainViewModel model)
        {
            model.OpenRelative(path);
        }
    }
}
