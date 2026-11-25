using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mpm.App;

/// <summary>Small modal dialogs (Avalonia has no built-in message box).</summary>
internal static class Dialogs
{
    public static Task<bool> Confirm(Window owner, string title, string message, string yes, string no = "Cancel") =>
        Show(owner, title, message, yes, no);

    public static Task Inform(Window owner, string title, string message) => Show(owner, title, message, "OK", null);

    private static async Task<bool> Show(Window owner, string title, string message, string yes, string? no)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 700,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yesButton = new Button { Content = yes, IsDefault = true, MinWidth = 90 };
        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(yesButton);
        if (no is not null)
        {
            var noButton = new Button { Content = no, IsCancel = true, MinWidth = 90 };
            noButton.Click += (_, _) => dialog.Close();
            buttons.Children.Add(noButton);
        }
        var body = new DockPanel { Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Avalonia.Thickness(0, 12, 0, 0);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer
        {
            Content = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            MaxHeight = 560,
        });
        dialog.Content = body;
        await dialog.ShowDialog(owner);
        return result;
    }
}
