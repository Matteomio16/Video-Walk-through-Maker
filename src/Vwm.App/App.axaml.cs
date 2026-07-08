using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Vwm.App.ViewModels;

namespace Vwm.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            ApplyCommandLine(vm, desktop.Args ?? []);
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Optional prefill: --video path, --script-file path, --out path, --auto-analyze.</summary>
    private static void ApplyCommandLine(MainViewModel vm, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--video" when i + 1 < args.Length:
                    vm.VideoPath = args[++i];
                    break;
                case "--script-file" when i + 1 < args.Length:
                    vm.ScriptText = File.ReadAllText(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    vm.OutputPath = args[++i];
                    break;
                case "--auto-analyze":
                    vm.AnalyzeCommand.Execute(null);
                    break;
            }
        }
    }
}
