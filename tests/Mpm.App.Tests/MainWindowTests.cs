using Avalonia.Controls;
using Xunit;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mpm.Core.Dbc;
using Mpm.Core.Mpq;
using Mpm.Core.Session;
using Mpm.Core.Store;

namespace Mpm.App.Tests;

public class MainWindowTests
{
    private const string Titles = "DBFilesClient\\CharTitles.dbc";

    private static byte[] TitlesDbc(params (int Id, string Name)[] rows)
    {
        var layout = DbcLayouts.For335().Find("CharTitles")!;
        var pool = new StringPool();
        var nameCol = layout.Columns.ToList().FindIndex(c => c.Name == "Name_lang[0]");
        var list = rows.Select(r =>
        {
            var row = new long[layout.FieldCount];
            row[layout.IdColumn] = r.Id;
            row[nameCol] = pool.Intern(r.Name);
            return row;
        }).ToList();
        return new DbcTable(layout, pool, list).ToFile().ToBytes();
    }

    /// <summary>A tiny fake client plus two conflicting patches in a downloads folder.</summary>
    private static (string Root, string Wow, string Downloads) Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpm-app-tests-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "WoW", "Data");
        Directory.CreateDirectory(Path.Combine(data, "enUS"));
        MpqWriter.Write(Path.Combine(data, "patch.MPQ"), new Dictionary<string, byte[]> { ["Textures\\Stock.blp"] = [1] });
        MpqWriter.Write(Path.Combine(data, "enUS", "locale-enUS.MPQ"), new Dictionary<string, byte[]>
        {
            [Titles] = TitlesDbc((1, "Private"), (2, "Corporal")),
        });
        var downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        MpqWriter.Write(Path.Combine(downloads, "patch-A.MPQ"), new Dictionary<string, byte[]> { [Titles] = TitlesDbc((1, "From A"), (2, "Corporal")) });
        MpqWriter.Write(Path.Combine(downloads, "patch-B.MPQ"), new Dictionary<string, byte[]> { [Titles] = TitlesDbc((1, "From B"), (2, "Corporal")) });
        return (root, Path.Combine(root, "WoW"), downloads);
    }

    private static MainWindow NewWindow(string root) =>
        new(new MergeSession(new ModStore(Path.Combine(root, "app", "store")), Path.Combine(root, "app")));

    [AvaloniaFact]
    public void StartsWithNothingToMerge()
    {
        var (root, _, _) = Setup();
        var window = NewWindow(root);
        window.Show();
        Assert.False(window.CanMerge);
        Assert.False(window.CanSave);
        Assert.Contains("Choose your WoW", window.StatusText);
        Directory.Delete(root, true);
    }

    [AvaloniaFact]
    public async Task DropMergeAnswer_AppliesTheAnswer()
    {
        var (root, wow, downloads) = Setup();
        var window = NewWindow(root);
        window.Show();

        await window.OpenClient(wow);
        await window.DropPaths([downloads]);
        Assert.True(window.CanMerge);

        await window.Merge();
        Assert.True(window.CanSave);
        Assert.Contains("question", window.StatusText);

        // The conflict card offers both patches; B is preselected (later wins). Pick A and re-merge.
        var radios = window.QuestionsPanel.GetVisualDescendants().OfType<RadioButton>().ToList();
        var pickA = radios.First(r => (r.Content as string ?? "").StartsWith("patch-A"));
        Assert.Contains(radios, r => r.IsChecked == true && (r.Content as string ?? "").StartsWith("patch-B"));
        pickA.IsChecked = true;
        await window.ApplyAnswers();

        Dispatcher.UIThread.RunJobs();
        var after = window.QuestionsPanel.GetVisualDescendants().OfType<RadioButton>().First(r => (r.Content as string ?? "").StartsWith("patch-A"));
        Assert.True(after.IsChecked);

        // Render a frame so layout problems show up in a screenshot.
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var shot = Environment.GetEnvironmentVariable("MPM_SCREENSHOT");
        if (!string.IsNullOrEmpty(shot))
        {
            frame!.Save(shot);
        }
        window.Close();
        Directory.Delete(root, true);
    }
}
