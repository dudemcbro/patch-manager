using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Mpm.Core.Session;
using Mpm.Core.Store;

namespace Mpm.App;

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(new MergeSession(new ModStore(ModStore.DefaultRoot())));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
