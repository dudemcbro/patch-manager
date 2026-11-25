using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mpm.Core.Build;
using Mpm.Core.Session;

namespace Mpm.App;

/// <summary>
/// The whole app in one window: 1. choose a WoW client, 2. add patches (the client's own, a folder tree,
/// or drag and drop), 3. merge, answer questions, then save the single merged patch or install it.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MergeSession _session;
    private readonly TextBlock _clientLabel = new() { Text = "No client chosen", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _dropHint = new()
    {
        Text = "Drop .mpq or .zip files, or folders, here",
        Opacity = 0.6,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };
    private readonly StackPanel _questions = new() { Spacing = 10, Margin = new Thickness(0, 0, 16, 0) };

    internal StackPanel QuestionsPanel => _questions;
    internal string StatusText => _status.Text ?? "";
    internal bool CanMerge => _merge.IsEnabled;
    internal bool CanSave => _save.IsEnabled;
    private readonly TextBox _report = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
    };
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, IsVisible = false, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Button> _buttons = [];
    private readonly Dictionary<string, string> _pendingAnswers = new(StringComparer.Ordinal);

    private Button _merge = null!, _save = null!, _install = null!, _uninstall = null!, _addClient = null!;

    public MainWindow(MergeSession session)
    {
        _session = session;
        Title = "Mod Patch Manager";
        Width = 1100;
        Height = 780;
        MinWidth = 820;
        MinHeight = 560;
        Content = BuildLayout();
        Refresh();
        SetStatus("Choose your WoW 3.3.5a folder to start.");
    }

    // ---- layout -----------------------------------------------------------------------------

    private Control BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(Header("1. WoW client"));
        var clientRow = new DockPanel();
        var chooseClient = MakeButton("Choose WoW folder...", ChooseClient, alwaysEnabled: true);
        DockPanel.SetDock(chooseClient, Dock.Right);
        clientRow.Children.Add(chooseClient);
        clientRow.Children.Add(_clientLabel);
        top.Children.Add(clientRow);

        top.Children.Add(Header("2. Patches to merge"));
        top.Children.Add(new TextBlock
        {
            Text = "Later in the list wins when patches disagree (you will be asked about each disagreement).",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        var addRow = new WrapPanel { Orientation = Orientation.Horizontal };
        _addClient = MakeButton("Add this client's patches", AddClientPatches);
        addRow.Children.Add(_addClient);
        addRow.Children.Add(MakeButton("Add folder...", AddFolder));
        addRow.Children.Add(MakeButton("Add files...", AddFiles));
        addRow.Children.Add(MakeButton("Move up", () => MoveSelected(-1)));
        addRow.Children.Add(MakeButton("Move down", () => MoveSelected(1)));
        addRow.Children.Add(MakeButton("Remove", RemoveSelected));
        top.Children.Add(addRow);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _merge = MakeButton("Merge", Merge);
        _save = MakeButton("Save merged patch...", Save);
        _install = MakeButton("Install into client", Install);
        _uninstall = MakeButton("Uninstall from client", Uninstall);
        actions.Children.Add(_busy);
        actions.Children.Add(_merge);
        actions.Children.Add(_save);
        actions.Children.Add(_install);
        actions.Children.Add(_uninstall);
        DockPanel.SetDock(actions, Dock.Right);
        bottom.Children.Add(actions);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var dropZone = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Child = new Panel { Children = { _list, _dropHint } },
        };
        DragDrop.SetAllowDrop(dropZone, true);
        dropZone.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        dropZone.AddHandler(DragDrop.DropEvent, OnDrop);

        _tabs.Items.Add(new TabItem { Header = "Questions", Content = new ScrollViewer { Content = _questions } });
        _tabs.Items.Add(new TabItem { Header = "Report", Content = _report });

        var center = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,8,3*"), Margin = new Thickness(0, 8, 0, 0) };
        center.Children.Add(dropZone);
        Grid.SetColumn(_tabs, 2);
        center.Children.Add(_tabs);
        root.Children.Add(center);
        return root;
    }

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 16, Margin = new Thickness(0, 6, 0, 0) };

    private Button MakeButton(string text, Func<Task> action, bool alwaysEnabled = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        b.Click += async (_, _) => await action();
        b.Tag = alwaysEnabled ? "always" : null;
        _buttons.Add(b);
        return b;
    }

    private Button MakeButton(string text, Action action) => MakeButton(text, () => { action(); return Task.CompletedTask; });

    // ---- state ------------------------------------------------------------------------------

    private bool _isBusy;

    private void Refresh()
    {
        var hasClient = _session.Client is not null;
        var built = _session.LastBuild is not null;
        var installed = hasClient && _session.Installed is not null;
        foreach (var b in _buttons)
        {
            b.IsEnabled = !_isBusy && (b.Tag as string == "always" || hasClient);
        }
        if (!_isBusy)
        {
            _merge.IsEnabled = hasClient && _session.Sources.Count > 0;
            _save.IsEnabled = built;
            _install.IsEnabled = built && !installed;
            _uninstall.IsEnabled = installed;
        }

        _clientLabel.Text = _session.Client is { } c
            ? $"{c.DataDir}   (locale {_session.Locale}{(installed ? ", merged patch installed" : "")})"
            : "No client chosen";

        var tiers = _session.LastBuild?.Report.Tiers.ToDictionary(t => t.ModId, t => t.Tier.ToString().ToLowerInvariant());
        var selected = _list.SelectedIndex;
        _list.ItemsSource = _session.Sources.Select((s, i) =>
        {
            var tier = tiers is not null && tiers.TryGetValue(s.Mod.Id, out var t) ? $"  [{t}]" : "";
            var incomplete = s.Mod.IsComplete ? "" : $"  ({s.Mod.UnnamedFiles} unidentified files)";
            return $"{i + 1}. {Path.GetFileName(s.Path)}{tier}  -  {s.Mod.Files.Count} files{incomplete}";
        }).ToList();
        _list.SelectedIndex = Math.Min(selected, _session.Sources.Count - 1);
        _dropHint.IsVisible = _session.Sources.Count == 0;
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>Runs work off the UI thread with the window locked; shows errors in a dialog.</summary>
    private async Task Busy(string label, Func<string?> work)
    {
        _isBusy = true;
        _busy.IsVisible = true;
        SetStatus(label);
        Refresh();
        try
        {
            var message = await Task.Run(work);
            SetStatus(message ?? "Done.");
        }
        catch (Exception e)
        {
            SetStatus("Something went wrong.");
            await Dialogs.Inform(this, "Error", e.Message);
        }
        finally
        {
            _isBusy = false;
            _busy.IsVisible = false;
            Refresh();
        }
    }

    private static string Summarize(List<string> warnings, string ok) =>
        warnings.Count == 0 ? ok : $"{ok} {warnings.Count} note(s): {string.Join(" | ", warnings.Take(3))}{(warnings.Count > 3 ? " | ..." : "")}";

    // ---- actions ----------------------------------------------------------------------------

    private async Task ChooseClient()
    {
        var folder = await PickFolder("Choose your WoW 3.3.5a folder (the one with Wow.exe, or its Data folder)");
        if (folder is not null)
        {
            await OpenClient(folder);
        }
    }

    /// <summary>Opens a client folder (also used by tests, since pickers cannot run headless).</summary>
    internal async Task OpenClient(string folder)
    {
        await Busy("Opening client...", () =>
        {
            var warnings = _session.SetClient(folder);
            return Summarize(warnings, _session.Sources.Count > 0 ? $"Restored {_session.Sources.Count} patch(es) from last time." : "Client ready. Now add patches.");
        });
        ClearResults();
    }

    private Task AddClientPatches() => Busy("Reading the client's patches...", () =>
        Summarize(_session.AddClientPatches(), $"{_session.Sources.Count} patch(es) in the list."));

    private async Task AddFolder()
    {
        var folder = await PickFolder("Choose a folder of patches (subfolders are included)");
        if (folder is not null)
        {
            await Busy("Reading patches...", () => Summarize(_session.AddFolder(folder), $"{_session.Sources.Count} patch(es) in the list."));
        }
    }

    private async Task AddFiles()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose patches",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Patches (.mpq, .zip)") { Patterns = ["*.mpq", "*.MPQ", "*.zip", "*.ZIP"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            await Busy("Reading patches...", () => Summarize(_session.AddPaths(paths), $"{_session.Sources.Count} patch(es) in the list."));
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_session.Client is null)
        {
            SetStatus("Choose your WoW folder first, then drop patches.");
            return;
        }
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        await DropPaths(paths);
    }

    /// <summary>Adds dropped files and folders (also used by tests).</summary>
    internal async Task DropPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            await Busy("Reading dropped patches...", () => Summarize(_session.AddPaths(paths), $"{_session.Sources.Count} patch(es) in the list."));
            ClearResults();
        }
    }

    private void MoveSelected(int delta)
    {
        var i = _list.SelectedIndex;
        _session.Move(i, delta);
        Refresh();
        _list.SelectedIndex = Math.Clamp(i + delta, 0, _session.Sources.Count - 1);
        ClearResults();
    }

    private void RemoveSelected()
    {
        _session.Remove(_list.SelectedIndex);
        Refresh();
        ClearResults();
    }

    internal async Task Merge()
    {
        await Busy("Merging...", () =>
        {
            var r = _session.Merge().Report;
            return MergeStatus(r);
        });
        ShowResults();
    }

    private static string MergeStatus(BuildReport r)
    {
        var issues = r.Questions.Count(q => q.Kind != "tier");
        return $"Merged {r.Files.Count} file(s) from {r.ModOrder.Count} patch(es). " +
               (issues == 0 ? "No conflicts." : $"{issues} question(s) to review; the recommended choices are already applied.");
    }

    internal async Task ApplyAnswers()
    {
        var answers = new Dictionary<string, string>(_pendingAnswers);
        await Busy("Merging with your answers...", () => MergeStatus(_session.Answer(answers).Report));
        _pendingAnswers.Clear();
        ShowResults();
    }

    private async Task ResetAnswers()
    {
        _session.Answers.Clear();
        _pendingAnswers.Clear();
        await Merge();
    }

    private async Task Save()
    {
        var folder = await PickFolder("Choose where to save the merged patch");
        if (folder is not null)
        {
            await Busy("Saving...", () => $"Saved to {_session.Save(folder)} (copy patch-Z.MPQ to Data and the locale file to Data/{_session.Locale}).");
        }
    }

    private async Task Install()
    {
        List<string> rename = [];
        await Busy("Checking the client...", () => { rename = _session.InstallPreview(); return "Ready to install."; });
        var data = _session.Client!.DataDir;
        var list = rename.Count == 0 ? "  (none)" : string.Join("\n", rename.Select(p => "  " + Path.GetRelativePath(data, p)));
        var ok = await Dialogs.Confirm(this, "Install merged patch",
            $"This installs the merged patch into\n{data}\n\nThese patches will be renamed to *.mpm-off so the game stops loading them " +
            $"(nothing is deleted; Uninstall puts them back):\n{list}\n\nClose the game before continuing.", "Install");
        if (ok)
        {
            await Busy("Installing...", () =>
            {
                var ledger = _session.Install();
                return $"Installed {ledger.Installed.Count} archive(s); renamed {ledger.Renamed.Count} patch(es).";
            });
        }
    }

    private async Task Uninstall()
    {
        var ok = await Dialogs.Confirm(this, "Uninstall merged patch",
            "This removes the merged patch from the client and renames your original patches back. Close the game before continuing.", "Uninstall");
        if (!ok)
        {
            return;
        }
        UnapplyResultHolder? holder = null;
        await Busy("Uninstalling...", () =>
        {
            var r = _session.Uninstall();
            holder = new UnapplyResultHolder(r.Problems);
            return $"Removed {r.Removed.Count} archive(s); restored {r.Restored.Count} patch(es).";
        });
        if (holder is { Problems.Count: > 0 } h)
        {
            await Dialogs.Inform(this, "Uninstall finished with notes", string.Join("\n", h.Problems));
        }
    }

    private sealed record UnapplyResultHolder(IReadOnlyList<string> Problems);

    private async Task<string?> PickFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    // ---- results ----------------------------------------------------------------------------

    private void ClearResults()
    {
        _questions.Children.Clear();
        _report.Text = "";
        _pendingAnswers.Clear();
        Refresh();
    }

    private void ShowResults()
    {
        _questions.Children.Clear();
        _pendingAnswers.Clear();
        if (_session.LastBuild is not { } build)
        {
            return;
        }
        var report = build.Report;
        _report.Text = BuildOutput.Summarize(report, report.Files.Select(f => f.Chain).Distinct()
            .Select(c => BuildOutput.ArchivePath(c, BuildOutput.DefaultSlot)).ToList());

        var issues = report.Questions.Where(q => q.Kind != "tier").ToList();
        var tiers = report.Questions.Where(q => q.Kind == "tier").ToList();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "Apply answers and merge again", IsEnabled = false };
        apply.Click += async (_, _) => await ApplyAnswers();
        var reset = new Button { Content = "Reset to recommended" };
        reset.Click += async (_, _) => await ResetAnswers();
        bar.Children.Add(apply);
        bar.Children.Add(reset);
        _questions.Children.Add(bar);

        _questions.Children.Add(Header(issues.Count == 0 ? "No conflicts" : $"{issues.Count} thing(s) to decide"));
        if (issues.Count == 0)
        {
            _questions.Children.Add(new TextBlock { Text = "All patches merged without disagreements.", TextWrapping = TextWrapping.Wrap });
        }
        foreach (var q in issues)
        {
            _questions.Children.Add(QuestionCard(q, apply));
        }
        if (tiers.Count > 0)
        {
            _questions.Children.Add(Header("Where does each patch come from?"));
            _questions.Children.Add(new TextBlock
            {
                Text = "Server patches win over local mods, and local mods may only change visuals on server data. The guesses below come from what each patch changes.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
            foreach (var q in tiers)
            {
                _questions.Children.Add(QuestionCard(q, apply));
            }
        }
        _tabs.SelectedIndex = 0;
    }

    private Control QuestionCard(Question q, Button apply)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = q.Prompt, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        if (!string.IsNullOrEmpty(q.Detail))
        {
            panel.Children.Add(new TextBlock { Text = q.Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 });
        }
        var options = new StackPanel { Orientation = q.Options.Count <= 2 ? Orientation.Horizontal : Orientation.Vertical, Spacing = 12 };
        foreach (var option in q.Options)
        {
            var radio = new RadioButton
            {
                GroupName = q.Id,
                Content = option.Value == q.Default ? $"{option.Label} (recommended)" : option.Label,
                IsChecked = option.Value == q.Chosen,
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true)
                {
                    return;
                }
                if (option.Value == q.Chosen)
                {
                    _pendingAnswers.Remove(q.Id);
                }
                else
                {
                    _pendingAnswers[q.Id] = option.Value;
                }
                apply.IsEnabled = _pendingAnswers.Count > 0;
            };
            options.Children.Add(radio);
        }
        panel.Children.Add(options);
        return new Border
        {
            Child = panel,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = q.Chosen == q.Default ? Brushes.Gray : Brushes.SteelBlue,
        };
    }
}
