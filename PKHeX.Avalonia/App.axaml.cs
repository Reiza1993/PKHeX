using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PKHeX.Core;

namespace PKHeX.Avalonia;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Load localized game strings (species, moves, items...) used by the editor.
        GameInfo.CurrentLanguage = GameLanguage.DefaultLanguage;
        SaveHandlerRetroArchGBA.Register(); // RetroArch VBA-Next/gpSP .srm layout
        Updater.CleanupOldFiles();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var path = desktop.Args?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(path))
                window.Opened += (_, _) => window.OpenFile(path);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
