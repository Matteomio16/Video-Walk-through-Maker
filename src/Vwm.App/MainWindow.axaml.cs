using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Vwm.App.ViewModels;

namespace Vwm.App;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        foreach (var item in e.Data.GetFiles() ?? [])
        {
            var path = item.TryGetLocalPath();
            if (path is null)
                continue;
            if (Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md")
                Vm.ScriptText = await File.ReadAllTextAsync(path);
            else
                Vm.VideoPath = path;
        }
    }

    private async void OnBrowseVideo(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the video recording",
            FileTypeFilter =
            [
                new FilePickerFileType("Videos") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is string path)
            Vm.VideoPath = path;
    }

    private async void OnBrowseScript(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the script file",
            FileTypeFilter =
            [
                new FilePickerFileType("Text") { Patterns = ["*.txt", "*.md"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is string path)
            Vm.ScriptText = await File.ReadAllTextAsync(path);
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save narrated video as",
            DefaultExtension = "mp4",
            SuggestedFileName = "walkthrough_narrated.mp4",
        });
        if (file?.TryGetLocalPath() is string path)
            Vm.OutputPath = path;
    }
}
