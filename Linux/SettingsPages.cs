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

/// <summary>What the settings pages ask of the app. Every change is already in the settings object; the app only has to
/// act on it.</summary>
public interface ISettingsHost
{
    SettingsService Settings { get; }
    /// <summary>The characters that have a client running now.</summary>
    IReadOnlyCollection<string> RunningCharacters();
    /// <summary>A hotkey was changed: bind the keys again.</summary>
    void HotkeysChanged();
    /// <summary>A hotkey box starts or ends listening for a key: the global grabs are lifted meanwhile.</summary>
    void CaptureHotkey(bool capturing);
    /// <summary>An alert switch or a log setting was changed: apply the alert profiles and the log folders again.</summary>
    void AlertsChanged();
    /// <summary>A setting the tray menu shows was changed (the language, the lock): rebuild the menu.</summary>
    void TrayChanged();
    /// <summary>The settings file was replaced and loaded again: place the thumbnails, bind the keys, restart the alerts.</summary>
    void Imported();
    /// <summary>The voice the alerts are said with (the page lists its voices and tries them), or null before the alerts exist.</summary>
    LinuxSpeaker? Speaker { get; }
    /// <summary>A sentence was tried and came through: a run of failures that stopped the alerts' voice ends.</summary>
    void VoiceAnswered();
    /// <summary>Sends one test message to the saved Discord link. Refused when there is none this account can read.</summary>
    Task<PushResult> TestPush();
    /// <summary>What Discord last answered, or null before the first message.</summary>
    PushStatus? PushStatus { get; }
}

/// <summary>The settings window's pages. Each page is built from the settings as they are when it is shown, and every
/// control writes its change at once and asks for a delayed save.</summary>
internal sealed class SettingsPages
{
    private readonly ISettingsHost _host;
    private readonly Window _window;
    private readonly Action _refresh;
    private readonly Action _relocalize;
    /// <summary>The clash lines of the page that shows: each recomputes itself from the settings (a key was set, cleared or moved).</summary>
    private readonly List<Action> _clashLines = new();
    private CancellationTokenSource? _trying;

    /// <param name="refresh">Builds the page that is showing again (a group was added or removed).</param>
    /// <param name="relocalize">Builds the whole window again (the language changed).</param>
    public SettingsPages(ISettingsHost host, Window window, Action refresh, Action relocalize)
    {
        _host = host;
        _window = window;
        _refresh = refresh;
        _relocalize = relocalize;
    }

    private AppSettings S => _host.Settings.Settings;
    private static string T(string key, string english) => Ui.T(key, english);
    private void Save() => _host.Settings.SaveDelayed();

    private static StackPanel Page(string title)
    {
        var page = new StackPanel { Spacing = 2 };
        page.Children.Add(Ui.Heading(title));
        return page;
    }

    // ── General ──

    public Control General()
    {
        var page = Page(T("L.S.Page.app.general", "General"));

        page.Children.Add(Ui.Section(T("L.Linux.Display", "Language")));
        var languages = new ComboBox { Width = 240 };
        languages.Items.Add(T("L.Linux.Automatic", "Automatic"));
        foreach (var (_, name) in LocalizationService.Languages) languages.Items.Add(name);
        int at = Array.FindIndex(LocalizationService.Languages, l => l.Code == S.Language);
        languages.SelectedIndex = at + 1;   // -1 (an unknown or empty setting) is "Automatic", item 0
        languages.SelectionChanged += (_, _) =>
        {
            int i = languages.SelectedIndex;
            if (i < 0) return;
            string code = i == 0 ? "" : LocalizationService.Languages[i - 1].Code;
            if (code == S.Language) return;
            S.Language = code;
            LocalizationService.SetLanguage(code);
            Save();
            _host.TrayChanged();
            _relocalize();
        };
        page.Children.Add(Ui.Row(T("L.S.app.general.language", "Language"), languages, T("L.Linux.LanguageNote", "Automatic follows the language of this desktop.")));

        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.events.logs", "Reading EVE's logs")));
        page.Children.Add(Ui.Row(T("L.S.alerts.events.chatLog", "Read chat logs"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.EnableChatLogMonitoring, on => { S.EnableChatLogMonitoring = on; Save(); _host.AlertsChanged(); })));
        page.Children.Add(Ui.Row(T("L.S.alerts.events.gameLog", "Read game logs"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.EnableGameLogMonitoring, on => { S.EnableGameLogMonitoring = on; Save(); _host.AlertsChanged(); })));
        page.Children.Add(FolderRow(T("L.Linux.ChatFolder", "Chat log folder"), S.ChatLogDirectory, v => S.ChatLogDirectory = v));
        page.Children.Add(FolderRow(T("L.Linux.GameFolder", "Game log folder"), S.GameLogDirectory, v => S.GameLogDirectory = v));
        page.Children.Add(Ui.Note(T("L.Linux.FolderNote", "Left empty, the logs are looked for in the Steam (Proton) folder of EVE Online.")));

        page.Children.Add(Ui.Section(T("L.Linux.ImportTitle", "Settings from the Windows app")));
        page.Children.Add(Import());
        return page;
    }

    /// <summary>A folder path, typed or picked.</summary>
    private Control FolderRow(string label, string value, Action<string> store)
    {
        var path = Ui.Text(value, v => { store(v.Trim()); Save(); _host.AlertsChanged(); }, 300);
        var browse = new Button { Content = Ui.Plain(T("L.Common.Browse", "Browse")) + "…" };
        browse.Click += async (_, _) =>
        {
            var picked = await PickFolder(label);
            if (picked != null) path.Text = picked;
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(path);
        row.Children.Add(browse);
        return Ui.Row(label, row);
    }

    private async Task<string?> PickFolder(string title)
    {
        try
        {
            var folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { return null; }   // no file chooser on this desktop
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
    private Control Import()
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

    // ── Thumbnails ──

    public Control Thumbnails()
    {
        var page = Page(T("L.S.Nav.Thumbnails", "Thumbnails"));

        page.Children.Add(Ui.Section(T("L.S.Sec.thumbs.appearance.size", "Size")));
        var start = S.ThumbnailStartLocation;
        page.Children.Add(Ui.Row(T("L.S.thumbs.layout.startWidth", "Starting width"), Ui.Number(40, 4000, start.Width, v => { S.ThumbnailStartLocation.Width = v; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.layout.startHeight", "Starting height"), Ui.Number(30, 4000, start.Height, v => { S.ThumbnailStartLocation.Height = v; Save(); })));
        page.Children.Add(Ui.Note(T("L.Linux.StartNote", "The size a thumbnail gets the first time its client is seen.")));
        page.Children.Add(Ui.Note(T("L.Linux.ResizeNote", "To resize a thumbnail, hold the right mouse button on it, press the left button too and move the mouse. Every thumbnail follows; hold Ctrl to resize only that one.")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.appearance.opacity", "Opacity"),
            Ui.Slider(0, 255, S.ThumbnailOpacity, v => { S.ThumbnailOpacity = v; Save(); }, v => $"{v} ({OpacityPercent.FromByte(v)} %)")));

        page.Children.Add(Ui.Section(T("L.S.Sec.thumbs.text.labels", "Text on thumbnails")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.showText", "Character name"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.ShowThumbnailTextOverlay, on => { S.ShowThumbnailTextOverlay = on; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.color", "Text colour"), Ui.Colour(S.ThumbnailTextColor, v => { S.ThumbnailTextColor = v; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.size", "Text size"),
            Ui.Number(6, 72, ThumbnailFields.ReadSize(S.ThumbnailTextSize, 12), v => { S.ThumbnailTextSize = ThumbnailFields.WriteSize(v); Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.font", "Font"), FontBox()));
        // Streamer mode: names, labels and system names are masked wherever they are drawn, asked each time, so it takes effect at once.
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.streamer", "Streamer mode"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.StreamerMode, on => { S.StreamerMode = on; Save(); _host.TrayChanged(); }),
            T("L.S.thumbs.text.streamer.Desc", "Keeps what could give you away off a stream or a screenshot: character names show as Client 1, Client 2 and so on, and labels and system names are left out. Off by default.")));

        page.Children.Add(Ui.Section(T("L.S.Sec.thumbs.text.active", "The client you're playing")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.highlightColor", "Highlight colour"), Ui.Colour(S.ClientHighlightColor, v => { S.ClientHighlightColor = v; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.highlightWidth", "Highlight width"), Ui.Number(0, 40, S.ClientHighlightBorderThickness, v => { S.ClientHighlightBorderThickness = v; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.visibility.hideActive", "Hide the client you're playing"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.HideActiveThumbnail, on => { S.HideActiveThumbnail = on; Save(); })));

        page.Children.Add(Ui.Section(T("L.S.Sec.thumbs.text.others", "Other clients")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.inactiveColor", "Border colour"), Ui.Colour(S.InactiveClientBorderColor, v => { S.InactiveClientBorderColor = v; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.inactiveWidth", "Border width"), Ui.Number(0, 40, S.InactiveClientBorderThickness, v => { S.InactiveClientBorderThickness = v; Save(); }),
            T("L.S.thumbs.text.inactiveWidth.Desc", "0 hides the border.")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.groupBorders", "Group colours"),
            Ui.Check(T("L.S.clients.groups.showBorders", "Show group borders"), S.ShowAllColoredBorders, on => { S.ShowAllColoredBorders = on; Save(); })));

        page.Children.Add(Ui.Section(T("L.S.Sec.thumbs.visibility.stacking", "Staying on top")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.visibility.alwaysOnTop", "Always on top"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.ShowThumbnailsAlwaysOnTop, on => { S.ShowThumbnailsAlwaysOnTop = on; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.layout.lock", "Lock thumbnail positions"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.LockPositions, on => { S.LockPositions = on; Save(); _host.TrayChanged(); })));
        return page;
    }

    /// <summary>The thumbnail font: the system's fonts, with the stored name kept in the list even when this desktop
    /// lacks it (a name from a Windows file).</summary>
    private Control FontBox()
    {
        var fonts = new ComboBox { Width = 240 };
        var names = FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (!names.Contains(S.ThumbnailTextFont, StringComparer.OrdinalIgnoreCase)) names.Insert(0, S.ThumbnailTextFont);
        foreach (var name in names) fonts.Items.Add(name);
        fonts.SelectedItem = names.First(n => string.Equals(n, S.ThumbnailTextFont, StringComparison.OrdinalIgnoreCase));
        fonts.SelectionChanged += (_, _) =>
        {
            if (fonts.SelectedItem is not string name || name == S.ThumbnailTextFont) return;
            S.ThumbnailTextFont = name;
            Save();
        };
        return fonts;
    }

    // ── Characters ──

    public Control Characters()
    {
        _clashLines.Clear();
        var page = Page(T("L.S.Page.clients.characters", "Characters"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,220,210,Auto"), Margin = new Thickness(0, 6) };
        var host = new StackPanel { Spacing = 0 };
        page.Children.Add(Ui.Card(host));
        host.Children.Add(grid);

        int row = 0;
        void Cell(Control c, int column)
        {
            c.Margin = new Thickness(0, 3, 10, 3);
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            grid.Children.Add(c);
        }
        void Header()
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var (text, column) in new[] { (T("L.Common.Character", "Character"), 0), (T("L.Common.Label", "Label"), 1), (T("L.Common.Hotkey", "Hotkey"), 2), (T("L.Linux.Hidden", "Hidden"), 3) })
                Cell(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Opacity = 0.8 }, column);
            row++;
        }
        void Add(string name)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var running = _host.RunningCharacters().Contains(name, StringComparer.OrdinalIgnoreCase);
            Cell(new TextBlock { Text = name, Opacity = running ? 1 : 0.6 }, 0);
            var label = Ui.Text(CharacterFields.Label(S, name), v => { CharacterFields.SetLabel(S, name, v); Save(); }, 200);
            label.Margin = new Thickness(0);
            Cell(label, 1);
            var profile = CharacterDirectory.Current(S);
            Cell(HotkeyField("char:" + name, profile.Hotkeys.TryGetValue(name, out var b) ? b.Key : "", key =>
            {
                if (key.Length == 0) profile.Hotkeys.Remove(name);
                else profile.Hotkeys[name] = new HotkeyBinding { Key = key };
            }), 2);
            Cell(Ui.Check("", S.ThumbnailVisibility.TryGetValue(name, out var hidden) && hidden != 0, on =>
            {
                if (on) S.ThumbnailVisibility[name] = 1;
                else S.ThumbnailVisibility.Remove(name);
                Save();
            }), 3);
            row++;
        }

        var known = CharacterDirectory.Known(S, _host.RunningCharacters());
        if (known.Count == 0) host.Children.Insert(0, Ui.Note(T("L.Linux.NoCharacters", "No characters yet. They appear here once an EVE client is running.")));
        else
        {
            Header();
            foreach (var name in known) Add(name);
        }

        var newName = new TextBox { Width = 220, PlaceholderText = T("L.Common.Character", "Character") };
        var add = new Button { Content = Ui.Plain(T("L.Common.AddCharacter", "Add Character")) };
        add.Click += (_, _) =>
        {
            var name = newName.Text?.Trim() ?? "";
            if (name.Length == 0) return;
            if (!S.ThumbnailVisibility.ContainsKey(name)) S.ThumbnailVisibility[name] = 0;   // 0 = shown; it makes the name known
            Save();
            _refresh();
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        addRow.Children.Add(newName);
        addRow.Children.Add(add);
        page.Children.Add(addRow);
        return page;
    }

    // ── Groups ──

    public Control Groups()
    {
        _clashLines.Clear();
        var page = Page(T("L.S.Page.clients.groups", "Groups"));
        var groups = MergedGroups.Build(S);
        if (groups.Count == 0) page.Children.Add(Ui.Note(T("L.S.clients.groups.empty", "No groups yet.")));
        var known = CharacterDirectory.Known(S, _host.RunningCharacters());
        foreach (var group in groups) page.Children.Add(GroupCard(group, known));

        var name = new TextBox { Width = 240, PlaceholderText = T("L.S.clients.groups.new", "Make a group") };
        var make = new Button { Content = T("L.S.clients.groups.new", "Make a group") };
        var status = new TextBlock { Foreground = Brushes.IndianRed, VerticalAlignment = VerticalAlignment.Center };
        make.Click += (_, _) =>
        {
            if (MergedGroups.New(S, name.Text ?? "") == null)
            {
                status.Text = T("L.Linux.GroupNameUsed", "Type a name no group uses yet.");
                return;
            }
            Save();
            _refresh();
        };
        var makeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        makeRow.Children.Add(name);
        makeRow.Children.Add(make);
        makeRow.Children.Add(status);
        page.Children.Add(makeRow);
        return page;
    }

    private Control GroupCard(MergedGroup group, IReadOnlyList<string> known)
    {
        var body = new StackPanel { Spacing = 6 };

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var nameBox = new TextBox { Text = group.Name, FontWeight = FontWeight.SemiBold, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        void Rename()
        {
            var wanted = nameBox.Text?.Trim() ?? "";
            if (wanted == group.Name) return;
            if (MergedGroups.Rename(S, group, wanted)) { Save(); _host.HotkeysChanged(); }
            else nameBox.Text = group.Name;
        }
        nameBox.LostFocus += (_, _) => Rename();
        nameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Rename(); };
        title.Children.Add(nameBox);
        var delete = new Button { Content = T("L.Common.DeleteWord", "Delete") };
        delete.Click += (_, _) =>
        {
            MergedGroups.Delete(S, group, everywhere: true);
            Save();
            _host.HotkeysChanged();
            _refresh();
        };
        Grid.SetColumn(delete, 1);
        title.Children.Add(delete);
        body.Children.Add(title);

        body.Children.Add(Ui.Row(T("L.S.Events.ColColour", "Colour"), Ui.Colour(group.Colour?.Color ?? "", v => { MergedGroups.SetColor(S, group, v); Save(); })));
        body.Children.Add(Ui.Row(T("L.Linux.GroupNext", "Next key"), HotkeyField("group:" + group.Name + ":next", group.NextKey, key => GroupKey(group, true, key))));
        body.Children.Add(Ui.Row(T("L.Linux.GroupPrevious", "Previous key"), HotkeyField("group:" + group.Name + ":previous", group.PreviousKey, key => GroupKey(group, false, key))));

        var members = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var member in known.Concat(group.Members).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tick = Ui.Check(member, group.Members.Contains(member, StringComparer.OrdinalIgnoreCase), on =>
            {
                if (on) MergedGroups.AddMember(group, member);
                else MergedGroups.RemoveMember(group, member);
                Save();
                _host.HotkeysChanged();
            });
            tick.Margin = new Thickness(0, 0, 14, 0);
            members.Children.Add(tick);
        }
        body.Children.Add(Ui.Row(T("L.Linux.GroupMembers", "Members"), members));
        return Ui.Card(body);
    }

    private void GroupKey(MergedGroup group, bool next, string key)
    {
        if (!MergedGroups.SetKey(S, group, next, key)) _refresh();   // another group of that name owns the key half: show what is stored
    }

    // ── Hotkeys ──

    public Control Hotkeys()
    {
        _clashLines.Clear();
        var page = Page(T("L.S.Page.clients.hotkeys", "Hotkeys"));
        // GlobalHotkeys, as on Windows: off = the keys are grabbed only while EVE or MultiPreview is in front (the
        // thumbnails' tick lets them go otherwise), so a bound "1" still types in other programs.
        var scope = new ComboBox { Width = 280 };
        scope.Items.Add(T("L.S.Opt.ScopeEve", "Only while EVE is in front"));
        scope.Items.Add(T("L.S.Opt.ScopeEverywhere", "Everywhere"));
        scope.SelectedIndex = S.GlobalHotkeys ? 1 : 0;
        scope.SelectionChanged += (_, _) =>
        {
            bool everywhere = scope.SelectedIndex == 1;
            if (scope.SelectedIndex < 0 || everywhere == S.GlobalHotkeys) return;
            S.GlobalHotkeys = everywhere;
            Save();
        };
        page.Children.Add(Ui.Row(T("L.S.clients.hotkeys.scope", "Hotkeys work"), scope,
            T("L.S.clients.hotkeys.scope.Desc", "Only while EVE is in front leaves your keys alone in other apps.")));
        page.Children.Add(Ui.Section(T("L.S.Sec.clients.hotkeys.cycling", "Switching between clients")));
        page.Children.Add(Key(T("L.S.clients.hotkeys.cycleNext", "Next client"), S.GlobalCycleForwardHotkey, v => S.GlobalCycleForwardHotkey = v));
        page.Children.Add(Key(T("L.S.clients.hotkeys.cyclePrevious", "Previous client"), S.GlobalCycleBackwardHotkey, v => S.GlobalCycleBackwardHotkey = v));
        page.Children.Add(Key(T("L.S.clients.hotkeys.backToPrevious", "Back to the client before this one"), S.PreviousClientHotkey, v => S.PreviousClientHotkey = v));
        page.Children.Add(Key(T("L.S.clients.hotkeys.lastAlert", "Go to the last alert"), S.GoToLastAlertHotkey, v => S.GoToLastAlertHotkey = v,
            T("L.S.clients.hotkeys.lastAlert.Desc", "Switches to the client with the most severe alert you have not looked at yet. Press it again for the next one.")));
        page.Children.Add(Ui.Section(T("L.S.Sec.clients.hotkeys.app", "MultiPreview hotkeys")));
        page.Children.Add(Key(T("L.S.clients.hotkeys.hideAll", "Hide or show all thumbnails"), S.HideShowThumbnailsHotkey, v => S.HideShowThumbnailsHotkey = v));
        page.Children.Add(Key(T("L.S.clients.hotkeys.lockPositions", "Lock or unlock thumbnail positions"), S.LockPositionsHotkey, v => S.LockPositionsHotkey = v));
        page.Children.Add(Key(T("L.S.alerts.sounds.dndHotkey", "Do not disturb on or off"), S.DoNotDisturbHotkey, v => S.DoNotDisturbHotkey = v,
            T("L.S.alerts.sounds.dndHotkey.Desc", "Mutes alert sounds, spoken alerts and the wrap-around sound, and holds alerts sent to Discord, until you press it again. Borders, badges and toasts go on.")));
        page.Children.Add(Ui.Note(T("L.Linux.HotkeyNote", "Click a box and press the keys. Escape clears it. A key for one character is set on Characters; keys for a group on Groups.")));
        return page;
    }

    private Control Key(string label, string value, Action<string> store, string? description = null) =>
        Ui.Row(label, HotkeyField("global:" + label, value, store), description);

    /// <summary>A hotkey box. A change is stored by <paramref name="store"/>, saved and bound; then every clash line of the
    /// page is worked out again, because a key set here can make another box's key a clash. The key is still kept (the
    /// key bound last is the one that works).</summary>
    private Control HotkeyField(string id, string value, Action<string> store)
    {
        var row = Ui.Hotkey(value, key => { store(key); Save(); _host.HotkeysChanged(); foreach (var line in _clashLines.ToList()) line(); }, _host);
        var warning = new TextBlock { Foreground = Brushes.Orange, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, IsVisible = false };
        void Show()
        {
            var others = HotkeyClashes.With(AllHotkeys(), id);
            warning.IsVisible = others.Count > 0;
            warning.Text = others.Count == 0 ? "" : string.Format(T("L.Linux.HotkeyClash", "Same key as: {0}. Only one of them gets it."), string.Join(", ", others));
        }
        _clashLines.Add(Show);
        Show();
        // Under the box: beside it the line would run out of the window (and out of the Characters table's column).
        var field = new StackPanel { Spacing = 2 };
        field.Children.Add(row);
        field.Children.Add(warning);
        return field;
    }

    /// <summary>Every key MultiPreview binds, with the action that owns it: the characters' keys, the groups' two keys, the five global ones.</summary>
    private List<(string Id, string Label, string Key)> AllHotkeys()
    {
        var all = new List<(string, string, string)>();
        foreach (var (character, binding) in CharacterDirectory.Current(S).Hotkeys) all.Add(("char:" + character, character, binding.Key));
        foreach (var (name, group) in S.HotkeyGroups)
        {
            all.Add(("group:" + name + ":next", name + " – " + T("L.Linux.GroupNext", "Next key"), group.ForwardsHotkey));
            all.Add(("group:" + name + ":previous", name + " – " + T("L.Linux.GroupPrevious", "Previous key"), group.BackwardsHotkey));
        }
        all.Add(("global:" + T("L.S.clients.hotkeys.cycleNext", "Next client"), T("L.S.clients.hotkeys.cycleNext", "Next client"), S.GlobalCycleForwardHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.cyclePrevious", "Previous client"), T("L.S.clients.hotkeys.cyclePrevious", "Previous client"), S.GlobalCycleBackwardHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.backToPrevious", "Back to the client before this one"), T("L.S.clients.hotkeys.backToPrevious", "Back to the client before this one"), S.PreviousClientHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.hideAll", "Hide or show all thumbnails"), T("L.S.clients.hotkeys.hideAll", "Hide or show all thumbnails"), S.HideShowThumbnailsHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.lockPositions", "Lock or unlock thumbnail positions"), T("L.S.clients.hotkeys.lockPositions", "Lock or unlock thumbnail positions"), S.LockPositionsHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.lastAlert", "Go to the last alert"), T("L.S.clients.hotkeys.lastAlert", "Go to the last alert"), S.GoToLastAlertHotkey));
        all.Add(("global:" + T("L.S.alerts.sounds.dndHotkey", "Do not disturb on or off"), T("L.S.alerts.sounds.dndHotkey", "Do not disturb on or off"), S.DoNotDisturbHotkey));
        return all;
    }

    // ── Alerts ──

    /// <summary>"From which severity" (AlertWays.Reaches): critical only, critical and warning, all.</summary>
    private Control FromBox(int value, Action<int> set)
    {
        var box = new ComboBox { Width = 240 };
        foreach (var key in new[] { "L.S.Opt.FromCritical", "L.S.Opt.FromWarning", "L.S.Opt.FromInfo" }) box.Items.Add(T(key, key));
        box.SelectedIndex = Math.Clamp(value, 0, 2);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(box.SelectedIndex); Save(); } };
        return box;
    }

    /// <summary>The @here / @everyone a pushed message with a critical line starts with: none, 1, 2.</summary>
    private Control MentionBox()
    {
        var box = new ComboBox { Width = 240 };
        foreach (var key in new[] { "L.S.Opt.PushMentionNone", "L.S.Opt.PushMentionHere", "L.S.Opt.PushMentionEveryone" }) box.Items.Add(T(key, key));
        box.SelectedIndex = S.PushMention is 1 or 2 ? S.PushMention : 0;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { S.PushMention = box.SelectedIndex; Save(); } };
        return box;
    }

    /// <summary>For each severity a voice ("Automatic" or one of espeak-ng's) and a button that tries it. The voices are read
    /// off the UI thread (the first ask starts espeak-ng); until they are in, the lists show only "Automatic".</summary>
    private Control Voices()
    {
        var card = new StackPanel { Spacing = 2 };
        var installed = new List<SpeechVoice>();
        var boxes = new List<(string Severity, ComboBox Box)>();
        var speaker = _host.Speaker;
        var failed = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        bool building = false;

        string Label(SpeechVoice v) => v.Name + " (" + v.Language + ")";
        void Fill(string severity, ComboBox box)
        {
            building = true;
            try
            {
                box.Items.Clear();
                var auto = SpeechVoiceChoice.Automatic(installed, LocalizationService.CurrentLanguage);
                box.Items.Add(string.Format(T("L.S.Speech.Automatic", "Automatic: {0}"), auto != null ? Label(auto) : T("L.Linux.SpeechDefaultVoice", "the system's default voice")));
                foreach (var v in installed) box.Items.Add(Label(v));
                SpeechVoiceChoice.Read(S.SpeechVoices).TryGetValue(severity, out var chosen);
                box.SelectedIndex = chosen == null ? 0 : installed.FindIndex(v => v.Id == chosen) + 1;   // a voice this system no longer has shows as Automatic
            }
            finally { building = false; }
        }
        void Try(string severity)
        {
            if (speaker == null) return;
            _trying?.Cancel();
            var stop = _trying = new CancellationTokenSource();
            failed.IsVisible = false;
            string voice = SpeechVoiceChoice.For(severity, S.SpeechVoices, installed, LocalizationService.CurrentLanguage);
            _ = speaker.SayAsync(T("L.S.Speech.Example", "This is how an alert sounds."), voice, SpeechOptions.Of(S).Volume, stop.Token).ContinueWith(done =>
            {
                var thrown = done.Exception;   // looked at, so that nothing is left unobserved
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_trying, stop)) return;   // only the last try counts
                    _trying = null;
                    if (thrown != null)
                    {
                        failed.Text = T("L.Linux.SpeechTryFailed", "The voice could not say that. Check that speech-dispatcher or espeak-ng works and that a sound device is connected.");
                        failed.IsVisible = true;
                    }
                    else if (done.IsCompletedSuccessfully) _host.VoiceAnswered();
                });
            }, TaskScheduler.Default);
        }

        foreach (var severity in AlertEvents.Severities)
        {
            string name = T(AlertEvents.SeverityKey(severity), severity);
            var box = new ComboBox { Width = 300 };
            Fill(severity, box);
            box.SelectionChanged += (_, _) =>
            {
                if (building || box.SelectedIndex < 0) return;
                var text = SpeechVoiceChoice.Set(S.SpeechVoices, severity, box.SelectedIndex == 0 ? null : installed[box.SelectedIndex - 1].Id);
                if (text == S.SpeechVoices) return;
                S.SpeechVoices = text;
                Save();
            };
            boxes.Add((severity, box));
            var tryIt = new Button { Content = T("L.S.Speech.Try", "Try") };
            ToolTip.SetTip(tryIt, string.Format(T("L.S.Speech.TryFor", "Try the voice for {0} alerts"), name));
            tryIt.Click += (_, _) => Try(severity);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(box);
            line.Children.Add(tryIt);
            card.Children.Add(Ui.Row(string.Format(T("L.S.Speech.VoiceFor", "Voice for {0} alerts"), name), line));
        }
        card.Children.Add(failed);
        var (spd, espeak) = LinuxSpeaker.Installed();
        if (!spd && !espeak)
            card.Children.Add(new TextBlock
            {
                Text = T("L.Linux.SpeechNoProgram", "No speech program found, so nothing can be spoken. Install speech-dispatcher (spd-say) or espeak-ng."),
                Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4),
            });
        card.DetachedFromVisualTree += (_, _) => { _trying?.Cancel(); _trying = null; };

        if (speaker != null)
            _ = Task.Run(speaker.Voices).ContinueWith(done =>
            {
                if (!done.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    installed.AddRange(done.Result);
                    foreach (var (severity, box) in boxes) Fill(severity, box);
                });
            }, TaskScheduler.Default);
        return card;
    }

    /// <summary>The Discord webhook: the box that takes the link, and the buttons to save it, send a test message and remove
    /// it. The link is a secret. It is kept in a file only the owner can read (WebhookFile), never in the settings file
    /// (which holds a marker), the box is emptied once the link is saved, and only the masked link is shown again. A link
    /// the Windows app encrypted for its Windows user (dpapi1:) can't be read here, and the page says to paste it again.</summary>
    private Control Webhook()
    {
        var card = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4) };
        var shown = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, IsVisible = false };
        var box = new TextBox { Width = 420, MaxLength = 512, IsUndoEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = T("L.S.Push.LinkBox", "Discord webhook link") };
        var save = new Button { Content = Ui.NoAccess(T("L.S.Push.Save", "Save _link")) };
        var test = new Button { Content = Ui.NoAccess(T("L.S.Push.TestButton", "Send a _test message")) };
        var remove = new Button { Content = Ui.NoAccess(T("L.S.Push.Remove", "_Remove link")) };

        void Say(string text) { status.Text = text; status.IsVisible = text.Length > 0; }
        void ShowLink()
        {
            var stored = S.PushDiscordWebhook ?? "";
            Uri? link = null;
            if (stored.Length == 0) shown.Text = T("L.S.Push.NoLink", "No link yet. In Discord: Server Settings › Integrations › Webhooks › New Webhook › Copy Webhook URL, then paste it here.");
            else if (WebhookFile.IsWindowsEncrypted(stored))
                shown.Text = T("L.Linux.PushDpapi", "The saved link comes from the Windows app, where it is encrypted for that Windows user. It can't be read here: paste it again.");
            else if ((link = WebhookFile.Read(stored)) == null)
                shown.Text = T("L.S.Push.Unreadable", "The saved link can't be read here: it was saved on another PC or by another Windows user. Paste it again.");
            else
                shown.Text = DiscordWebhook.Masked(link) + "\n" + string.Format(T("L.Linux.PushKeptIn", "The link is kept in a file only you can read: {0}"), WebhookFile.FilePath);
            test.IsVisible = link != null;
            remove.IsVisible = stored.Length > 0;
        }
        void Show(PushStatus? last)
        {
            if (last == null) return;
            Say(last.Outcome switch
            {
                PushOutcome.Sent => string.Format(System.Globalization.CultureInfo.CurrentCulture, T("L.S.Push.Status.Sent", "The last message reached Discord at {0:t}."), last.AtUtc.ToLocalTime()),
                PushOutcome.Gone => T("L.S.Push.Status.Gone", "Discord says this webhook doesn't exist. Nothing is sent until you save a new link."),
                PushOutcome.Refused => string.Format(T("L.S.Push.Status.Refused", "Discord refused the last message ({0})."), last.Status),
                PushOutcome.RateLimited => T("L.S.Push.Status.Limited", "Discord asked MultiPreview to slow down. Messages wait their turn."),
                _ => T("L.S.Push.Status.Unreachable", "Discord could not be reached. MultiPreview keeps trying for 5 minutes per alert."),
            });
        }
        void SaveLink()
        {
            if (!DiscordWebhook.TryParse(box.Text, out var link))
            {
                Say(T("L.S.Push.NotAWebhook", "That is not a Discord webhook link. It looks like https://discord.com/api/webhooks/…"));
                return;
            }
            string kept;
            try { kept = WebhookFile.Save(link); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say(T("L.Linux.PushWriteFailed", "The link could not be saved: its file could not be written."));
                return;
            }
            S.PushDiscordWebhook = kept;
            Save();
            _host.AlertsChanged();
            box.Text = "";
            Say("");
            ShowLink();
        }
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; SaveLink(); } };
        save.Click += (_, _) => SaveLink();
        remove.Click += (_, _) =>
        {
            S.PushDiscordWebhook = "";
            WebhookFile.Delete();
            Save();
            _host.AlertsChanged();
            Say("");
            ShowLink();
        };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            PushResult result;
            try { result = await _host.TestPush(); }
            catch (Exception) { result = new PushResult(PushOutcome.Unreachable); }
            test.IsEnabled = true;
            Show(new PushStatus(result.Outcome, result.Status, DateTime.UtcNow));
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(test);
        buttons.Children.Add(remove);
        card.Children.Add(shown);
        card.Children.Add(box);
        card.Children.Add(buttons);
        card.Children.Add(status);
        ShowLink();
        Show(_host.PushStatus);
        return Ui.Card(card);
    }

    public Control Alerts()
    {
        var page = Page(T("L.S.Nav.Alerts", "Alerts"));

        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.events.list", "Events")));
        foreach (var group in AlertEvents.Groups)
        {
            var events = AlertEvents.Listed.Where(e => e.Group == group).ToList();
            if (events.Count == 0) continue;
            var table = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110") };
            int row = 0;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var heading = new TextBlock { Text = T("L.S.Events.Group." + group, group), FontWeight = FontWeight.SemiBold, Opacity = 0.85 };
            table.Children.Add(heading);
            var severityHeading = new TextBlock { Text = T("L.S.Events.ColSeverity", "Severity"), Opacity = 0.6 };
            Grid.SetColumn(severityHeading, 1);
            table.Children.Add(severityHeading);
            foreach (var ev in events)
            {
                row++;
                table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var tick = Ui.Check(T(ev.LabelKey, ev.Key), AlertFields.IsOn(S, ev.Key), on => { AlertFields.SetOn(S, ev.Key, on); Save(); _host.AlertsChanged(); });
                Grid.SetRow(tick, row);
                table.Children.Add(tick);
                var severity = new TextBlock
                {
                    Text = T(AlertEvents.SeverityKey(ev.Severity), ev.Severity),
                    Foreground = ev.Severity == AlertEvents.Critical ? Brushes.IndianRed : ev.Severity == AlertEvents.Warning ? Brushes.Orange : Brushes.LightSkyBlue,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetRow(severity, row);
                Grid.SetColumn(severity, 1);
                table.Children.Add(severity);
            }
            page.Children.Add(Ui.Card(table));
        }

        page.Children.Add(Ui.Section(T("L.Linux.Notifications", "Desktop notifications")));
        var notify = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        foreach (var severity in AlertEvents.Severities)
            notify.Children.Add(Ui.Check(T(AlertEvents.SeverityKey(severity), severity), AlertFields.HubToast(S, severity), on => { AlertFields.SetHubToast(S, severity, on); Save(); }));
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.notifyFrom", "Notify for which alerts"), notify));

        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.sounds.main", "Sounds")));
        page.Children.Add(Ui.Row(T("L.S.alerts.sounds.enabled", "Play alert sounds"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.EnableAlertSounds, on => { S.EnableAlertSounds = on; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.alerts.sounds.volume", "Volume"),
            Ui.Slider(0, 100, S.AlertSoundVolume, v => { S.AlertSoundVolume = v; Save(); }, v => v + " %")));

        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.sounds.speech", "Spoken alerts")));
        page.Children.Add(Ui.Row(T("L.S.alerts.sounds.speak", "Speak alerts"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.SpeakAlerts, on => { S.SpeakAlerts = on; Save(); _host.AlertsChanged(); })));
        page.Children.Add(Ui.Row(T("L.S.alerts.sounds.speakFrom", "Speak which alerts"), FromBox(S.SpeakAlertsFrom, v => S.SpeakAlertsFrom = v)));
        page.Children.Add(Ui.Row(T("L.S.alerts.sounds.speechVolume", "Voice volume"),
            Ui.Slider(0, 100, Math.Clamp(S.SpeechVolume, 0, 100), v => { S.SpeechVolume = v; Save(); }, v => v + " %")));
        page.Children.Add(Voices());

        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.hub.push", "Phone and Discord")));
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.push", "Send alerts to Discord"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.PushAlerts, on => { S.PushAlerts = on; Save(); _host.AlertsChanged(); })));
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.pushFrom", "Send which alerts"), FromBox(S.PushAlertsFrom, v => S.PushAlertsFrom = v)));
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.pushMention", "Mention on critical alerts"), MentionBox()));
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.pushAway", "Only while you're away from the PC"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.PushOnlyWhenAway, on => { S.PushOnlyWhenAway = on; Save(); }),
            T("L.Linux.PushAwayNote", "Away is read from the X server's idle time. Where the X server can't say, alerts are sent anyway.")));
        page.Children.Add(Webhook());

        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.hub.history", "History")));
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.keepHistory", "Keep an alert history"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.AlertHistoryEnabled, on => { S.AlertHistoryEnabled = on; Save(); _host.AlertsChanged(); })));
        return page;
    }
}
