using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Mpm.App.Tests.TestAppBuilder))]

namespace Mpm.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
