using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>App › General's commands example and App › Profiles' import from the Windows app (LinuxSupport.Additions).</summary>
internal sealed partial class SettingsEditors
{
    /// <summary>How other programs send a command (CommandSocket), and four examples to copy.</summary>
    internal static Control CommandsExample()
    {
        var panel = new StackPanel { Spacing = 2 };
        var example = new SelectableTextBlock { Text = string.Join(Environment.NewLine, "eve-multipreview --focus <character>", "eve-multipreview --cycle <group>", "eve-multipreview --focus-mode", "eve-multipreview --last-alert"),
            FontFamily = new FontFamily("monospace"), Margin = new Thickness(0, 4) };
        panel.Children.Add(Ui.Note(T("L.Linux.CommandsNote", "Run MultiPreview again with a command, for example below; a Stream Deck app (StreamController, streamdeck-ui, OpenDeck) runs it from a button. eve-multipreview --help lists every command.")));
        panel.Children.Add(example);
        return panel;
    }

    private async Task<string?> PickJson()
    {
        try
        {
            var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("L.Linux.ImportPick", "Choose the Windows app's EVE MultiPreview.json"),
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }, FilePickerFileTypes.All },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>Import: a file path (typed or picked) and the button that does it.</summary>
    internal Control Import()
    {
        var panel = new StackPanel { Spacing = 6 };
        var path = new TextBox { Width = 300, PlaceholderText = "EVE MultiPreview.json" };
        var browse = new Button { Content = Ui.Plain(T("L.Common.Browse", "Browse")) + "…" };
        browse.Click += async (_, _) => { if (await PickJson() is { } picked) path.Text = picked; };
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pathRow.Children.Add(path);
        pathRow.Children.Add(browse);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var go = new Button { Content = T("L.Linux.Import", "Import settings from Windows…"), HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(path.Text) && await PickJson() is { } picked) path.Text = picked;
            if (string.IsNullOrWhiteSpace(path.Text)) return;
            status.Text = ImportFrom(path.Text.Trim());
        };
        panel.Children.Add(Ui.Row(T("L.Linux.ImportFile", "Settings file"), pathRow));
        panel.Children.Add(go);
        panel.Children.Add(status);
        return panel;
    }

    /// <summary>Replaces the settings with the file's: checks that it reads as a settings file, saves the current one to
    /// Backups, copies the file in and loads it. Returns the line to show. A file that loads badly is put back from the
    /// backup.</summary>
    private string ImportFrom(string source)
    {
        var settings = _host.Settings;
        try
        {
            SettingsService.ParseForLoad(File.ReadAllText(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return T("L.Linux.ImportBad", "Not imported: that is not a readable EVE MultiPreview settings file.") + " (" + ex.GetType().Name + ")";
        }
        string backup = "";
        try
        {
            settings.Flush();   // a change still waiting for its delayed save goes into the backup
            if (File.Exists(settings.SettingsPath))
            {
                Directory.CreateDirectory(AppPaths.BackupsDir);
                backup = Path.Combine(AppPaths.BackupsDir, $"EVE MultiPreview {DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(settings.SettingsPath, backup, overwrite: true);
            }
            File.Copy(source, settings.SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return T("L.Linux.ImportFailed", "Not imported: the file could not be copied.") + " (" + ex.GetType().Name + ")";
        }
        settings.Load();
        if (!settings.LoadedSuccessfully)
        {
            if (backup.Length > 0) File.Copy(backup, settings.SettingsPath, overwrite: true);
            settings.Load();
            return T("L.Linux.ImportBad", "Not imported: that is not a readable EVE MultiPreview settings file.");
        }
        // The imported settings keep no link of ours (the Windows app's is empty or encrypted): the file would stay behind, unused.
        if (!(S.PushDiscordWebhook ?? "").StartsWith(WebhookFile.Marker, StringComparison.Ordinal)) WebhookFile.Delete();
        LocalizationService.SetLanguage(S.Language);
        _host.Imported();
        _relocalize();
        return T("L.Linux.ImportDone", "Imported. The old settings are in") + " " + (backup.Length > 0 ? backup : AppPaths.BackupsDir);
    }
}
