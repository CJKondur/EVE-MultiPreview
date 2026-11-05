using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>S8: the bug report before it is saved, the Avalonia port of Views/BugReportWindow (keep in sync). Every file of
/// the zip is listed and its text shown; nothing is written until Save as zip…, which asks where, and nothing is ever
/// sent. The names switch builds the files again. The files' text is shown, never used as a format.</summary>
public sealed class BugReportWindow : Window
{
    private readonly Func<bool, IReadOnlyList<BugReportFile>> _build;
    private readonly Action<string> _saved;
    private readonly Func<string, string> _redact;
    private readonly CheckBox _mask;
    private readonly ListBox _list;
    private readonly TextBox _text;
    private readonly TextBlock _status;
    private readonly Button _save;
    private bool _saving;

    private static string T(string key, string english) => LocalizationService.Str(LinuxSupport.Key(key), english);

    /// <param name="build">The files, names masked or not (LinuxBugReport.Gather).</param>
    /// <param name="saved">Told the path once the zip is written (the app opens its folder).</param>
    /// <param name="redact">What an error's text goes through before it is shown (LinuxBugReport.Redactor).</param>
    public BugReportWindow(Func<bool, IReadOnlyList<BugReportFile>> build, Action<string> saved, Func<string, string> redact)
    {
        _build = build;
        _saved = saved;
        _redact = redact;
        Title = T("L.Report.Title", "Bug report");
        Width = 860;
        Height = 600;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var top = new StackPanel();
        top.Children.Add(Line(T("L.Report.Intro", "This is everything the zip will hold. Read it before you share it: names MultiPreview doesn't know, such as other players in your logs, are not hidden.")));
        // Checked before the handler is attached, so the files are built once here.
        _mask = new CheckBox { Content = new TextBlock { Text = T("L.Report.MaskNames", "Hide character names") }, IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        top.Children.Add(_mask);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var note = Line(T("L.Report.Note", "Nothing is sent. The zip is saved where you choose, and you decide whom to give it to. Your user name, the PC's name and your Discord webhook are always left out."));
        note.Opacity = 0.75;
        bottom.Children.Add(note);
        _status = Line("");
        _status.Margin = new Thickness(0, 6, 0, 0);
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        _save = new Button { Content = new TextBlock { Text = Ui.NoAccess(T("L.Report.Save", "Save as zip…")) }, IsDefault = true, Padding = new Thickness(14, 4) };
        _save.Click += async (_, _) => await Save();
        var close = new Button { Content = new TextBlock { Text = Ui.NoAccess(T("L.Report.Close", "Close")) }, IsCancel = true, Padding = new Thickness(14, 4) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_save);
        buttons.Children.Add(close);
        bottom.Children.Add(buttons);

        _text = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("DejaVu Sans Mono, Liberation Mono, monospace"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        _list = new ListBox { Width = 220, Margin = new Thickness(0, 10, 10, 0) };
        _list.SelectionChanged += (_, _) => _text.Text = Shown;

        var dock = new DockPanel { Margin = new Thickness(16, 12), LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_list, Dock.Left);
        dock.Children.Add(top);
        dock.Children.Add(bottom);
        dock.Children.Add(_list);
        dock.Children.Add(_text);
        Content = dock;
        LinuxLooks.Dress(this);

        Rebuild(0);
        _mask.IsCheckedChanged += (_, _) => Rebuild(_list.SelectedIndex);
    }

    /// <summary>The files as they are now: what Save writes.</summary>
    public IReadOnlyList<BugReportFile> Files { get; private set; } = Array.Empty<BugReportFile>();

    private string Shown => _list.SelectedIndex is int i && i >= 0 && i < Files.Count ? Files[i].Text : "";

    private bool MaskNames => _mask.IsChecked == true;

    /// <summary>Asks where, then writes the zip there and only there: first beside it as path.tmp, then moved over it, so a
    /// write that fails half way leaves neither a cut zip nor a lost earlier file.</summary>
    private async Task Save()
    {
        if (Files.Count == 0 || _saving) return;
        _saving = true;
        try
        {
            string? path;
            try
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    SuggestedFileName = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"EVE MultiPreview bug report {DateTime.Now:yyyy-MM-dd HH-mm}.zip"),
                    DefaultExtension = "zip",
                    FileTypeChoices = new[] { new FilePickerFileType(T("L.Report.ZipFilter", "Zip files (*.zip)|*.zip").Split('|')[0]) { Patterns = new[] { "*.zip" } } },
                });
                path = file?.TryGetLocalPath();
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { path = null; }   // no file chooser on this desktop
            if (string.IsNullOrEmpty(path)) return;
            var files = Files;   // what the player saw when they clicked
            var temp = path + ".tmp";
            try
            {
                await Task.Run(() =>
                {
                    using (var stream = File.Create(temp)) BugReport.WriteZip(stream, files);
                    File.Move(temp, path, overwrite: true);
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                try { File.Delete(temp); } catch (Exception) { /* the status line says it failed */ }
                _status.Text = string.Format(T("L.Report.Failed", "Could not save the zip: {0}"), _redact(e.Message));
                return;
            }
            _status.Text = string.Format(T("L.Report.Saved", "Saved as {0}"), Path.GetFileName(path));
            _saved(path);
        }
        finally { _saving = false; }
    }

    // The files once more, the same file selected. A report that could not be made leaves nothing to save and says why.
    private void Rebuild(int selected)
    {
        try
        {
            Files = _build(MaskNames);
            _status.Text = "";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Files = Array.Empty<BugReportFile>();
            _status.Text = string.Format(T("L.Report.BuildFailed", "Could not make the report: {0}"), _redact(e.Message));
        }
        _list.Items.Clear();
        foreach (var file in Files) _list.Items.Add(new TextBlock { Text = file.Name });
        _save.IsEnabled = Files.Count > 0;
        _list.SelectedIndex = Files.Count == 0 ? -1 : Math.Clamp(selected, 0, Files.Count - 1);
        _text.Text = Shown;
    }

    private static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
