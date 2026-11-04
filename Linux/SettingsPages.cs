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
    /// <summary>Opens the alert history window (or brings the open one forward).</summary>
    void OpenAlertHistory();
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
    /// <summary>The events snoozed for one character, or null before the alerts exist.</summary>
    EventSnoozes? Snoozes { get; }
    /// <summary>Snoozes one event for one character (null length: until MultiPreview restarts).</summary>
    void Snooze(string character, string alertType, TimeSpan? length);
    /// <summary>"Edit area…" (CropAreaPick.Start): the settings window hides, the player drags over the character's
    /// running client, the part is stored in <paramref name="def"/> and <paramref name="picked"/> runs, and the window
    /// comes back. Returns the L.* key of why it couldn't start ({0} = the character), or null once the picker is open.</summary>
    string? PickCropArea(string character, CropDefinition def, Action picked);
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

        // IDEAS #39: the command socket asks it at every command, so a change only saves.
        page.Children.Add(Ui.Section(T("L.Linux.CommandsTitle", "Commands from other programs")));
        page.Children.Add(Ui.Row(T("L.S.app.general.acceptCommands", "Take commands from other programs"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.AcceptCommands, on => { S.AcceptCommands = on; Save(); }),
            T("L.Linux.AcceptCommands.Desc", "Lets a Stream Deck app, a desktop shortcut or a script tell the running MultiPreview to bring a character to the front, switch profile, or toggle thumbnails or focus mode. Only programs of your own account can.")));
        var example = new SelectableTextBlock { Text = string.Join(Environment.NewLine, "eve-multipreview --focus <character>", "eve-multipreview --cycle <group>", "eve-multipreview --focus-mode", "eve-multipreview --last-alert"),
            FontFamily = new FontFamily("monospace"), Margin = new Thickness(0, 4) };
        page.Children.Add(Ui.Note(T("L.Linux.CommandsNote", "Run MultiPreview again with a command, for example below; a Stream Deck app (StreamController, streamdeck-ui, OpenDeck) runs it from a button. eve-multipreview --help lists every command.")));
        page.Children.Add(example);

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
        // Read at each resize gesture (Previews.Tick hands LockResizeAspect and the floor to every thumbnail; ResizeOthers reads the other).
        page.Children.Add(Ui.Row(T("L.S.thumbs.appearance.individualResize", "Resize thumbnails one at a time"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.IndividualThumbnailResize, on => { S.IndividualThumbnailResize = on; Save(); }),
            T("L.S.thumbs.appearance.individualResize.Desc", "On: resizing a thumbnail changes only that one. Off: every thumbnail follows, unless you hold Ctrl.")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.appearance.lockAspect", "Keep the shape when resizing"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.LockResizeAspect, on => { S.LockResizeAspect = on; Save(); }),
            T("L.S.thumbs.appearance.lockAspect.Desc", "Resizing a thumbnail with the mouse keeps its width-to-height ratio.")));
        // The smallest size boxes show while the limit is on (the Windows page's ShowIf), 20-2000 px (ThumbnailManager.MinFloor/MaxFloor).
        var minimum = S.ThumbnailMinimumSize;
        var floorBoxes = new StackPanel { IsVisible = S.ThumbnailMinimumSizeEnabled };
        floorBoxes.Children.Add(Ui.Row(T("L.S.thumbs.appearance.minWidth", "Smallest width"), Ui.Number(ThumbnailWindow.FloorMin, ThumbnailWindow.FloorMax, minimum.Width, v => { S.ThumbnailMinimumSize.Width = v; Save(); })));
        floorBoxes.Children.Add(Ui.Row(T("L.S.thumbs.appearance.minHeight", "Smallest height"), Ui.Number(ThumbnailWindow.FloorMin, ThumbnailWindow.FloorMax, minimum.Height, v => { S.ThumbnailMinimumSize.Height = v; Save(); })));
        page.Children.Add(Ui.Row(T("L.S.thumbs.appearance.minSizeOn", "Limit how small thumbnails get"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.ThumbnailMinimumSizeEnabled, on => { S.ThumbnailMinimumSizeEnabled = on; floorBoxes.IsVisible = on; Save(); }),
            T("L.S.thumbs.appearance.minSizeOn.Desc", "Off: resizing a thumbnail by hand stops at 80 × 50. On: it stops at the size below.")));
        page.Children.Add(floorBoxes);
        page.Children.Add(Ui.Row(T("L.S.thumbs.appearance.opacity", "Opacity"),
            Ui.Slider(0, 255, S.ThumbnailOpacity, v => { S.ThumbnailOpacity = v; Save(); }, v => $"{v} ({OpacityPercent.FromByte(v)} %)")));
        // Read at every tick (Previews.Tick hands them to each thumbnail), so a change only saves.
        page.Children.Add(Ui.Row(T("L.S.thumbs.appearance.opacityOnHover", "Solid under the mouse"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.OpacityOnHover, on => { S.OpacityOnHover = on; Save(); }),
            T("L.S.thumbs.appearance.opacityOnHover.Desc", "A thumbnail turns fully opaque while the mouse is over it.")));
        var zoomSize = Ui.Row(T("L.S.thumbs.appearance.hoverScale", "Zoom size"),
            Ui.Slider((int)ThumbnailFields.ZoomMin, (int)ThumbnailFields.ZoomMax, (int)ThumbnailFields.ZoomPercent(S.HoverScale),
                v => { S.HoverScale = ThumbnailFields.ZoomScale(v); Save(); }, v => v + " %"),
            T("L.S.thumbs.appearance.hoverScale.Desc", "How big it grows, compared with its normal size."));
        zoomSize.IsVisible = S.ResizeThumbnailsOnHover;
        page.Children.Add(Ui.Row(T("L.S.thumbs.appearance.hoverZoom", "Zoom under the mouse"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.ResizeThumbnailsOnHover, on => { S.ResizeThumbnailsOnHover = on; zoomSize.IsVisible = on; Save(); }),
            T("L.S.thumbs.appearance.hoverZoom.Desc", "A thumbnail grows while the mouse rests on it.")));
        page.Children.Add(zoomSize);

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

        // IDEAS #38: the app's half-second look makes or closes the bar and holds the main thumbnails: a change only saves.
        page.Children.Add(Ui.Section(T("L.NameBar.Title", "Character name bar")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.nameBar", "Show a name bar instead of thumbnails"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.NameBarMode, on => { S.NameBarMode = on; Save(); }),
            T("L.S.thumbs.nameBar.Desc", "Hides the main thumbnails and shows one row of character buttons at the top centre of your main screen. Click a button to switch to that client; rest the pointer on it for a moment to see that client live.")));

        // IDEAS #22, #47: the app reads all three at each look (every 3 s), so a change only saves.
        page.Children.Add(Ui.Section(T("L.S.Sec.thumbs.text.clientState", "When a client stops")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.notResponding", "Show “Not responding” on a frozen client"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.ClientNotRespondingBadge, on => { S.ClientNotRespondingBadge = on; Save(); }),
            T("L.Linux.NotRespondingDesc", "When a client's window stops answering for 5 seconds, its thumbnail says so.")));
        var seconds = Ui.Row(T("L.S.thumbs.text.disconnectSeconds", "Disconnected after"),
            WithUnit(Ui.Number(ClientWatchOptions.MinSeconds, ClientWatchOptions.MaxSeconds, S.DisconnectedSeconds, v => { S.DisconnectedSeconds = v; Save(); }), T("L.S.Unit.Sec", "s")),
            T("L.S.thumbs.text.disconnectSeconds.Desc", "How long a client must be without its server connection before it is marked."));
        seconds.IsVisible = S.ClientDisconnectedBadge;
        page.Children.Add(Ui.Row(T("L.S.thumbs.text.disconnected", "Show “Disconnected” when a client loses the server"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.ClientDisconnectedBadge, on => { S.ClientDisconnectedBadge = on; seconds.IsVisible = on; Save(); }),
            T("L.S.thumbs.text.disconnected.Desc", "When a logged-in client has had no connection to EVE's server for the time below, its thumbnail says so.")));
        page.Children.Add(seconds);
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
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,170,210,Auto,Auto"), Margin = new Thickness(0, 6) };
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
            foreach (var (text, column) in new[] { (T("L.Common.Character", "Character"), 0), (T("L.Common.Label", "Label"), 1), (T("L.Common.Hotkey", "Hotkey"), 2), (T("L.Linux.Hidden", "Hidden"), 3), (T("L.S.character.pip", "Second thumbnail (PiP)"), 4) })
                Cell(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 150 }, column);
            row++;
        }
        void Add(string name)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var running = _host.RunningCharacters().Contains(name, StringComparer.OrdinalIgnoreCase);
            Cell(new TextBlock { Text = name, Opacity = running ? 1 : 0.6 }, 0);
            var label = Ui.Text(CharacterFields.Label(S, name), v => { CharacterFields.SetLabel(S, name, v); Save(); }, 155);
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
            // The second thumbnail (CharacterFields.SetPip: off keeps its place and size) and its opacity in percent (8–100).
            // The app makes or closes it within 2 s (SyncPips) and reads the opacity at every tick.
            var pipOpacity = Ui.Number(8, 100, CharacterFields.PipOpacity(S, name), v => { CharacterFields.SetPipOpacity(S, name, v); Save(); });
            pipOpacity.Width = 95;
            pipOpacity.IsEnabled = CharacterFields.PipOn(S, name);
            ToolTip.SetTip(pipOpacity, T("L.S.character.pipOpacity", "Second thumbnail opacity"));
            var pip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            pip.Children.Add(Ui.Check("", CharacterFields.PipOn(S, name), on => { CharacterFields.SetPip(S, name, on); pipOpacity.IsEnabled = on; Save(); }));
            pip.Children.Add(WithUnit(pipOpacity, T("L.S.Unit.Percent", "%")));
            Cell(pip, 4);
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

        // IDEAS #35: the key is bound with the others; the app reads the other two twice a second while focus mode is on.
        page.Children.Add(Ui.Section(T("L.S.Sec.clients.groups.focus", "Focus mode")));
        page.Children.Add(Ui.Row(T("L.S.clients.groups.focusKey", "Focus mode on or off"),
            HotkeyField("global:focus", S.FocusModeHotkey, v => S.FocusModeHotkey = v),
            T("L.S.clients.groups.focusKey.Desc", "Shows only the thumbnails of one group and hides the others, until you press it again.")));
        var chosen = new ComboBox { Width = 280 };
        chosen.Items.Add(T("L.S.Opt.FocusActiveGroup", "The group of the client you are in"));
        var names = groups.Select(g => g.Name).Distinct(StringComparer.Ordinal).ToList();
        // A chosen group that no longer exists stays listed, so the box shows what is stored (focus mode says it is gone).
        if (S.FocusModeGroup is { Length: > 0 } stored && !names.Contains(stored)) names.Add(stored);
        foreach (var n in names) chosen.Items.Add(n);
        chosen.SelectedIndex = string.IsNullOrEmpty(S.FocusModeGroup) ? 0 : names.IndexOf(S.FocusModeGroup) + 1;
        chosen.SelectionChanged += (_, _) =>
        {
            if (chosen.SelectedIndex < 0) return;
            S.FocusModeGroup = chosen.SelectedIndex == 0 ? "" : names[chosen.SelectedIndex - 1];
            Save();
        };
        page.Children.Add(Ui.Row(T("L.S.clients.groups.focusGroup", "Group to show"), chosen,
            T("L.S.clients.groups.focusGroup.Desc", "The group of the client you are in follows you as you switch clients; a group you choose stays the same.")));
        page.Children.Add(Ui.Row(T("L.S.clients.groups.focusAlerts", "Show a hidden thumbnail while its alert is unseen"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.FocusModeShowAlerts, on => { S.FocusModeShowAlerts = on; Save(); }),
            T("L.S.clients.groups.focusAlerts.Desc", "A client outside the group that raises an alert shows until you look at it.")));
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

    // ── Crop windows ──

    /// <summary>The character the Crop windows page edits; kept while the page is built again.</summary>
    private string? _cropCharacter;
    /// <summary>The crop windows as the page shows them (CropEdits.Signature): a crop window dragged on the screen changes
    /// them, and the page is built again to show its new place.</summary>
    private string _cropShown = "";

    public Control Crops()
    {
        var page = Page(T("L.S.Page.thumbs.crops", "Crop windows"));
        page.Children.Add(Ui.Note(T("L.S.Page.thumbs.crops.Desc", "Small windows that each show one part of a client, like its overview or local chat.")));
        page.Children.Add(Ui.Row(T("L.S.thumbs.crops.enabled", "Show crop windows"),
            Ui.Check("", S.CropEnabled, v => { S.CropEnabled = v; Save(); _refresh(); }),
            T("L.S.thumbs.crops.enabled.Desc", "Off hides every crop window and keeps it for next time.")));
        if (!S.CropEnabled)
            page.Children.Add(Ui.Note(T("L.S.thumbs.crops.off", "Crop windows are off. You can still set them up here; they appear when you turn them on.")));

        var characters = CropEdits.Characters(S, _host.RunningCharacters());
        if (characters.Count == 0)
        {
            page.Children.Add(Ui.Note(T("L.S.Crops.NoCharacters", "No characters yet. Start EVE and log in first.")));
            return page;
        }
        if (_cropCharacter == null || !characters.Contains(_cropCharacter, StringComparer.OrdinalIgnoreCase)) _cropCharacter = characters[0];
        var character = _cropCharacter;
        var pick = new ComboBox { Width = 280, ItemsSource = characters, SelectedItem = characters.First(c => string.Equals(c, character, StringComparison.OrdinalIgnoreCase)) };
        pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is string c && c != _cropCharacter) { _cropCharacter = c; _refresh(); } };
        page.Children.Add(Ui.Row(T("L.S.thumbs.crops.editor", "Character"), pick,
            T("L.S.thumbs.crops.editor.Desc", "Whose crop windows to edit. Every character MultiPreview knows is listed, running ones first.")));

        page.Children.Add(Ui.Section(T("L.S.Sec.thumbs.crops.list", "Your crop windows")));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4) };
        var crops = CropEdits.Of(S, character);
        if (crops.Count == 0)
            page.Children.Add(Ui.Note(string.Format(T("L.S.Crops.Empty", "{0} has no crop windows yet. Add one, then press Edit area while the client is running."), character)));
        foreach (var def in crops.ToList()) page.Children.Add(CropCard(character, def, characters, status));

        var add = new Button { Content = Ui.NoAccess(T("L.S.Act.thumbs.crops.add", "_Add a crop window")), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            CropEdits.Add(S, character, CropEdits.NextName(S, character, T("L.S.Crops.DefaultName", "Crop {0}")));
            Save();
            _refresh();
        };
        page.Children.Add(add);
        page.Children.Add(status);
        page.Children.Add(Ui.Note(T("L.S.Crops.Tip", "Drag a crop window with the right mouse button to move it; press the left button too to resize it.")));

        // A crop window moved or resized on the screen: build the page again so its boxes show where it is now.
        _cropShown = CropEdits.Signature(S, character);
        var watch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (CropEdits.Signature(S, character) != _cropShown) _refresh();
        });
        page.AttachedToVisualTree += (_, _) => watch.Start();
        page.DetachedFromVisualTree += (_, _) => watch.Stop();
        return page;
    }

    /// <summary>One crop window: its name, "Show the name", the part of the client and the window on screen (four numbers
    /// each), opacity, "Clicks pass through", and Edit area…, Copy to… and Delete….</summary>
    private Control CropCard(string character, CropDefinition def, IReadOnlyList<string> characters, TextBlock status)
    {
        void Changed() { Save(); _cropShown = CropEdits.Signature(S, character); }
        var body = new StackPanel { Spacing = 4 };

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = Ui.Text(def.Name, v => { def.Name = v; Changed(); }, 260);
        name.FontWeight = FontWeight.SemiBold;
        name.HorizontalAlignment = HorizontalAlignment.Left;
        title.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var edit = new Button { Content = T("L.S.Crops.EditArea", "Edit area…") };
        edit.Click += (_, _) =>
        {
            var error = _host.PickCropArea(character, def, () => { _cropShown = ""; });   // the page is built again when it shows
            status.Foreground = Brushes.IndianRed;
            status.Text = error == null ? "" : string.Format(T(error, error), character);
        };
        buttons.Children.Add(edit);
        buttons.Children.Add(CopyButton(character, def, characters, status));
        buttons.Children.Add(DeleteButton(character, def));
        Grid.SetColumn(buttons, 1);
        title.Children.Add(buttons);
        body.Children.Add(title);

        body.Children.Add(Ui.Row(T("L.S.Crops.ShowName", "Show the name"), Ui.Check("", def.ShowLabel, v => { def.ShowLabel = v; Changed(); })));
        body.Children.Add(Ui.Row(T("L.S.Crops.Area", "Part of the client"),
            Rect((def.SourceX, def.SourceY, def.SourceWidth, def.SourceHeight), CropEdits.SourceMin, r => { CropEdits.SetSource(def, r); Changed(); })));
        body.Children.Add(Ui.Row(T("L.S.Crops.Window", "Window on screen"),
            Rect((def.PopupX, def.PopupY, def.PopupWidth, def.PopupHeight), CropEdits.PopupMin, r => { CropEdits.SetPopup(def, r); Changed(); })));
        body.Children.Add(Ui.Row(T("L.S.Crops.Opacity", "Opacity"),
            Ui.Slider(CropEdits.MinOpacity, CropEdits.MaxOpacity, def.Opacity, v => { def.Opacity = v; Changed(); }, v => v + " %")));
        body.Children.Add(Ui.Row(T("L.S.Crops.ClickThrough", "Clicks pass through"), Ui.Check("", def.ClickThrough, v => { def.ClickThrough = v; Changed(); })));
        return Ui.Card(body);
    }

    /// <summary>X, Y, width and height boxes: each held to its minimum and CropEdits.Limit.</summary>
    private static Control Rect((int X, int Y, int W, int H) value, (int X, int Y, int W, int H) min, Action<(int X, int Y, int W, int H)> set)
    {
        // Two lines, X and Y over width and height, so the boxes line up in every language.
        var r = value;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        void Box(int at, string label, int low, int v, Action<int> store)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Opacity = 0.8 };
            var box = Ui.Number(low, CropEdits.Limit, v, n => { store(n); set(r); });
            box.Width = 120;
            box.Margin = new Thickness(0, 2, 16, 2);
            Grid.SetRow(text, at / 2);
            Grid.SetColumn(text, at % 2 * 2);
            Grid.SetRow(box, at / 2);
            Grid.SetColumn(box, at % 2 * 2 + 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
        }
        Box(0, T("L.S.Slot.X", "X"), min.X, r.X, n => r.X = n);
        Box(1, T("L.S.Slot.Y", "Y"), min.Y, r.Y, n => r.Y = n);
        Box(2, T("L.S.Slot.W", "Width"), min.W, r.W, n => r.W = n);
        Box(3, T("L.S.Slot.H", "Height"), min.H, r.H, n => r.H = n);
        return grid;
    }

    /// <summary>"Copy to…" (CropsEditor.ShowCopyMenu): all other characters, or one of them.</summary>
    private Control CopyButton(string character, CropDefinition def, IReadOnlyList<string> characters, TextBlock status)
    {
        var others = characters.Where(c => !string.Equals(c, character, StringComparison.OrdinalIgnoreCase)).ToList();
        var copy = new Button { Content = T("L.S.Crops.CopyTo", "Copy to…") };
        var menu = new ContextMenu();
        void CopyTo(IReadOnlyList<string> targets)
        {
            int made = CropEdits.CopyTo(S, def, targets);
            Save();
            status.Foreground = Brushes.LightGreen;
            status.Text = made == 1
                ? string.Format(T("L.S.Crops.Copied", "Copied '{0}' to {1}."), def.Name, targets[0])
                : string.Format(T("L.S.Crops.CopiedMany", "Copied '{0}' to {1} characters."), def.Name, made);
        }
        if (others.Count == 0) menu.Items.Add(new MenuItem { Header = new TextBlock { Text = T("L.S.Crops.CopyNone", "No other characters") }, IsEnabled = false });
        else
        {
            var all = new MenuItem { Header = new TextBlock { Text = string.Format(T("L.S.Crops.CopyAll", "All other characters ({0})"), others.Count) } };
            all.Click += (_, _) => CopyTo(others);
            menu.Items.Add(all);
            menu.Items.Add(new Separator());
            foreach (var other in others)
            {
                var item = new MenuItem { Header = new TextBlock { Text = other } };
                item.Click += (_, _) => CopyTo(new[] { other });
                menu.Items.Add(item);
            }
        }
        copy.Click += (_, _) => menu.Open(copy);
        return copy;
    }

    /// <summary>"Delete…": asks first (L.S.Crops.DeleteConfirm), as the Windows page does.</summary>
    private Control DeleteButton(string character, CropDefinition def)
    {
        var delete = new Button { Content = T("L.S.Crops.Delete", "Delete…") };
        var yes = new Button { Content = T("L.Common.DeleteWord", "Delete") };
        var no = new Button { Content = T("L.Common.Cancel", "Cancel") };
        var ask = new StackPanel { Spacing = 8, MaxWidth = 320 };
        ask.Children.Add(new TextBlock { Text = T("L.S.Crops.DeleteTitle", "Delete crop window"), FontWeight = FontWeight.SemiBold });
        ask.Children.Add(new TextBlock { Text = string.Format(T("L.S.Crops.DeleteConfirm", "Delete the crop window '{0}'?"), def.Name), TextWrapping = TextWrapping.Wrap });
        var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        answers.Children.Add(yes);
        answers.Children.Add(no);
        ask.Children.Add(answers);
        var flyout = new Flyout { Content = ask };
        delete.Flyout = flyout;
        no.Click += (_, _) => flyout.Hide();
        yes.Click += (_, _) =>
        {
            flyout.Hide();
            CropEdits.Delete(S, character, def.Id);
            Save();
            _refresh();
        };
        return delete;
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
        page.Children.Add(Key(T("L.S.clients.hotkeys.hidePip", "Hide or show second thumbnails"), S.HideSecondaryHotkey, v => S.HideSecondaryHotkey = v));
        page.Children.Add(Key(T("L.S.clients.hotkeys.commandPalette", "Open the command palette"), S.CommandPaletteHotkey, v => S.CommandPaletteHotkey = v,
            T("L.S.clients.hotkeys.commandPalette.Desc", "Opens a small box over the monitor your mouse is on. Type a few letters of a character, label, group or profile and press Enter to switch to it. Press the key again or Esc to close it.")));
        page.Children.Add(Key(T("L.S.clients.hotkeys.hideCrops", "Hide or show crop windows"), S.HideShowCropsHotkey, v => S.HideShowCropsHotkey = v));
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
        all.Add(("global:" + T("L.S.clients.hotkeys.hidePip", "Hide or show second thumbnails"), T("L.S.clients.hotkeys.hidePip", "Hide or show second thumbnails"), S.HideSecondaryHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.commandPalette", "Open the command palette"), T("L.S.clients.hotkeys.commandPalette", "Open the command palette"), S.CommandPaletteHotkey));
        all.Add(("global:focus", T("L.S.clients.groups.focusKey", "Focus mode on or off"), S.FocusModeHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.hideCrops", "Hide or show crop windows"), T("L.S.clients.hotkeys.hideCrops", "Hide or show crop windows"), S.HideShowCropsHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.lockPositions", "Lock or unlock thumbnail positions"), T("L.S.clients.hotkeys.lockPositions", "Lock or unlock thumbnail positions"), S.LockPositionsHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.lastAlert", "Go to the last alert"), T("L.S.clients.hotkeys.lastAlert", "Go to the last alert"), S.GoToLastAlertHotkey));
        all.Add(("global:" + T("L.S.alerts.sounds.dndHotkey", "Do not disturb on or off"), T("L.S.alerts.sounds.dndHotkey", "Do not disturb on or off"), S.DoNotDisturbHotkey));
        return all;
    }

    // ── Alerts ──

    /// <summary>A number box with its unit written after it.</summary>
    private static Control WithUnit(Control box, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = unit, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

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

    /// <summary>The Windows SnoozesEditor: one line per snooze (character · event · time left) with End, End all, and a row to
    /// add one (character, event, length; SnoozeAdd decides what may be made). Built again after every change here.</summary>
    private Control Snoozed()
    {
        var card = new StackPanel { Spacing = 4 };
        var holder = Ui.Card(card);
        var snoozes = _host.Snoozes;
        if (snoozes == null) return holder;
        void Build()
        {
            card.Children.Clear();
            var now = DateTime.UtcNow;
            var active = snoozes.Active();
            if (active.Count == 0) card.Children.Add(Ui.Note(T("L.S.Snoozes.None", "Nothing is snoozed.")));
            foreach (var snooze in active)
            {
                var line = new DockPanel();
                var end = new Button { Content = T("L.S.Snoozes.End", "End"), Padding = new Thickness(10, 2) };
                var (who, key) = (snooze.Character, snooze.EventKey);
                ToolTip.SetTip(end, string.Format(T("L.S.Snoozes.EndFor", "End the snooze of {0} for {1}"),
                    EveMultiPreview.Views.AlertHub.EventDisplayName(key), StreamerMask.Name(StreamerKind.Character, who)));
                end.Click += (_, _) => { snoozes.End(who, key); Build(); };
                DockPanel.SetDock(end, Dock.Right);
                line.Children.Add(end);
                line.Children.Add(new TextBlock
                {
                    Text = StreamerMask.Name(StreamerKind.Character, who) + " · " + ThumbnailMenu.SnoozeLine(snooze, now),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                card.Children.Add(line);
            }
            if (active.Count > 1)
            {
                var all = new Button { Content = Ui.NoAccess(T("L.S.Snoozes.EndAll", "_End all")) };
                all.Click += (_, _) => { snoozes.EndAll(); Build(); };
                card.Children.Add(all);
            }

            // Add: a running or known character (or a typed name), an event, a length.
            var who2 = new AutoCompleteBox
            {
                Width = 200,
                PlaceholderText = T("L.S.Snoozes.AddCharacter", "Character"),
                ItemsSource = CharacterDirectory.Known(S, _host.RunningCharacters()).ToList(),
                FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            };
            var events = AlertEvents.Listed.Select(e => (e.Key, Name: T(e.LabelKey, e.Key))).ToList();
            var ev = new ComboBox { Width = 220, ItemsSource = events.Select(e => e.Name).ToList(), PlaceholderText = T("L.S.Snoozes.AddEvent", "Event") };
            var length = new ComboBox { Width = 170, ItemsSource = SnoozeAdd.Lengths.Select(l => Ui.NoAccess(T(l.TextKey, l.TextKey))).ToList(), SelectedIndex = 0 };
            var add = new Button { Content = Ui.NoAccess(T("L.S.Snoozes.Add", "_Add snooze")) };
            var refused = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            add.Click += (_, _) =>
            {
                var made = SnoozeAdd.TryBuild(who2.Text, ev.SelectedIndex >= 0 ? events[ev.SelectedIndex].Key : null,
                    SnoozeAdd.Lengths[Math.Max(0, length.SelectedIndex)].Length);
                if (made is not { } m)
                {
                    refused.Text = T("L.S.Snoozes.AddRefused", "Nothing was snoozed: choose an event and enter a character name (not too long, no line breaks or special characters).");
                    refused.IsVisible = true;
                    return;
                }
                _host.Snooze(m.Character, m.EventKey, m.Length);
                Build();
            };
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var c in new Control[] { who2, ev, length, add }) { c.Margin = new Thickness(0, 0, 8, 4); row.Children.Add(c); }
            card.Children.Add(row);
            card.Children.Add(refused);
        }
        Build();
        return holder;
    }

    /// <summary>The numbers of the events MultiPreview works out itself (the Windows Events page's cards under the table):
    /// each is read where the event is judged, so a change only saves. Each box shows the number used, the stored one held
    /// to its range; shown whether the events are on or not, with the notes that say when they apply.</summary>
    private Control EventNumbers()
    {
        var body = new StackPanel { Spacing = 2 };
        void Number(string key, string english, string desc, int min, int max, int value, string unitKey, string unit, Action<int> set) =>
            body.Children.Add(Ui.Row(T(key, english), WithUnit(Ui.Number(min, max, Math.Clamp(value, min, max), v => { set(v); Save(); }), T(unitKey, unit)), T(key + ".Desc", desc)));

        Number("L.S.alerts.events.stragglerWait", "Left behind: wait after the last jump", "Used only while Left behind is on.",
            StragglerWatch.MinWaitSeconds, StragglerWatch.MaxWaitSeconds, S.StragglerWaitSeconds, "L.S.Unit.Sec", "s", v => S.StragglerWaitSeconds = v);
        body.Children.Add(Ui.Note(T("L.S.alerts.events.ewarNote", "Capacitor neutralized and Jammed are off until you switch them on, and are read from an English game client only.")));
        Number("L.S.alerts.events.dpsThreshold", "Incoming DPS high: above", "The damage per second a character must take, on average, for the alert.",
            CombatOptions.MinDps, CombatOptions.MaxDps, S.DpsAlertThreshold, "L.S.Unit.Dps", "DPS", v => S.DpsAlertThreshold = v);
        Number("L.S.alerts.events.dpsSeconds", "Incoming DPS high: for", "How long the damage must stay that high before the alert.",
            CombatOptions.MinSeconds, CombatOptions.MaxSeconds, S.DpsAlertSeconds, "L.S.Unit.Sec", "s", v => S.DpsAlertSeconds = v);
        Number("L.S.alerts.events.repsSeconds", "Reps stopped: after", "How long after the last remote repair a hit raises the alert.",
            CombatOptions.MinRepsSeconds, CombatOptions.MaxRepsSeconds, S.RepsStoppedSeconds, "L.S.Unit.Sec", "s", v => S.RepsStoppedSeconds = v);
        body.Children.Add(Ui.Note(T("L.S.alerts.events.combatNote", "Incoming DPS high and Reps stopped are off until you turn them on in the table above. Both count NPC damage, also in PvE mode.")));
        Number("L.S.alerts.events.memoryLimit", "Client memory high: above", "The memory a client may use before the event is raised.",
            MemoryOptions.MinLimitMb, MemoryOptions.MaxLimitMb, S.MemoryAlertMb, "L.S.Unit.Mb", "MB", v => S.MemoryAlertMb = v);
        Number("L.S.alerts.events.memoryGrowth", "Client memory high: grows by", "How much a client's memory may grow within the time below before the event is raised.",
            MemoryOptions.MinGrowthMb, MemoryOptions.MaxGrowthMb, S.MemoryGrowthMb, "L.S.Unit.Mb", "MB", v => S.MemoryGrowthMb = v);
        Number("L.S.alerts.events.memoryMinutes", "Client memory high: within", "The time the growth is measured over.",
            MemoryOptions.MinMinutes, MemoryOptions.MaxMinutes, S.MemoryGrowthMinutes, "L.S.Unit.Min", "min", v => S.MemoryGrowthMinutes = v);
        body.Children.Add(Ui.Note(T("L.Linux.ClientNote", "Client not responding is off until you turn it on in the table above. It comes after a client has been frozen for 30 seconds.")));
        body.Children.Add(Ui.Note(T("L.Linux.ClosedNote", "Client closed unexpectedly is not available on Linux: Linux does not tell MultiPreview how a client it did not start ended, so a crash looks the same as quitting.")));
        Number("L.S.alerts.events.idleMinutes", "Character idle: after", "How long a character that was fighting, mining or repairing may go without it before the event is raised.",
            IdleWatch.MinMinutes, IdleWatch.MaxMinutes, S.IdleAlertMinutes, "L.S.Unit.Min", "min", v => S.IdleAlertMinutes = v);
        return Ui.Card(body);
    }

    public Control Alerts()
    {
        var page = Page(T("L.S.Nav.Alerts", "Alerts"));

        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.events.list", "Events")));
        foreach (var group in AlertEvents.Groups)
        {
            // "Client closed unexpectedly" needs the client's exit code, which Linux gives only to the program that started it.
            var events = AlertEvents.Listed.Where(e => e.Group == group && e.Key != ClientWatch.ClosedEvent).ToList();
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
        page.Children.Add(EventNumbers());

        // IDEAS #30: what is snoozed right now, with a way to end it or add one, and whether timed snoozes outlive a restart.
        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.events.snoozes", "Snoozed alerts")));
        page.Children.Add(Snoozed());
        page.Children.Add(Ui.Row(T("L.S.alerts.events.keepSnoozes", "Keep snoozes when MultiPreview restarts"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.KeepAlertSnoozes, on => { S.KeepAlertSnoozes = on; Save(); _host.AlertsChanged(); }),
            T("L.S.alerts.events.keepSnoozes.Desc", "Off: every snooze ends when MultiPreview closes. On: a 15-minute or 1-hour snooze keeps running after a restart.")));

        // IDEAS #13: AlertEnlarge reads all four at every alert that was shown and at its tick (EnlargeOptions.Of), so a change
        // only saves. The two numbers are used held to their ranges, and each box shows the number that is used. The options
        // show while the switch is on, as on the Windows page.
        page.Children.Add(Ui.Section(T("L.S.Sec.alerts.look.thumbnail", "On the thumbnail")));
        var enlargeOptions = new StackPanel { IsVisible = S.EnlargeOnAlert };
        page.Children.Add(Ui.Row(T("L.S.alerts.look.enlarge", "Enlarge the thumbnail on an alert"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.EnlargeOnAlert, on => { S.EnlargeOnAlert = on; enlargeOptions.IsVisible = on; Save(); }),
            T("L.S.alerts.look.enlarge.Desc", "The thumbnail grows for a few seconds, so you see what is happening there without switching.")));
        enlargeOptions.Children.Add(Ui.Row(T("L.S.alerts.look.enlargeFrom", "Enlarge for which alerts"), FromBox(S.EnlargeOnAlertFrom, v => S.EnlargeOnAlertFrom = v),
            T("L.S.alerts.look.enlargeFrom.Desc", "By severity: every event is critical, warning or info.")));
        enlargeOptions.Children.Add(Ui.Row(T("L.S.alerts.look.enlargePercent", "Enlarged size"),
            WithUnit(Ui.Number(EnlargeOptions.MinPercent, EnlargeOptions.MaxPercent, Math.Clamp(S.EnlargeOnAlertPercent, EnlargeOptions.MinPercent, EnlargeOptions.MaxPercent),
                v => { S.EnlargeOnAlertPercent = v; Save(); }), T("L.S.Unit.Percent", "%")),
            T("L.S.alerts.look.enlargePercent.Desc", "In percent of the thumbnail's own size. It is never larger than the monitor.")));
        enlargeOptions.Children.Add(Ui.Row(T("L.S.alerts.look.enlargeSeconds", "Stays enlarged for"),
            WithUnit(Ui.Number(EnlargeOptions.MinSeconds, EnlargeOptions.MaxSeconds, Math.Clamp(S.EnlargeOnAlertSeconds, EnlargeOptions.MinSeconds, EnlargeOptions.MaxSeconds),
                v => { S.EnlargeOnAlertSeconds = v; Save(); }), T("L.S.Unit.Sec", "s")),
            T("L.S.alerts.look.enlargeSeconds.Desc", "Seconds from the alert. It goes back at once when you switch to that client.")));
        page.Children.Add(enlargeOptions);

        page.Children.Add(Ui.Section(T("L.Linux.Notifications", "Desktop notifications")));
        // AlertHubEnabled is the switch the dispatcher asks before any toast (the Windows hub's): without it on, no notification shows.
        page.Children.Add(Ui.Row(T("L.Linux.NotifyOn", "Show desktop notifications"),
            Ui.Check(T("L.Common.Enabled", "Enabled"), S.AlertHubEnabled, on => { S.AlertHubEnabled = on; Save(); }),
            T("L.Linux.NotifyOn.Desc", "Each alert you choose below shows a notification, with buttons to switch to that client or to snooze the alert for 15 minutes.")));
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
        // Windows' alerts.hub.historyDays: kept days, 1-365; older day files go when MultiPreview starts (AlertHistory.DeleteOldDays).
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.historyDays", "Keep alerts for"),
            WithUnit(Ui.Number(AlertHistory.MinKeepDays, AlertHistory.MaxKeepDays, S.AlertHistoryDays, v => { S.AlertHistoryDays = v; Save(); _host.AlertsChanged(); }),
                T("L.S.Unit.Days", "days")),
            T("L.S.alerts.hub.historyDays.Desc", "Older days are deleted when MultiPreview starts.")));
        page.Children.Add(Ui.Note(string.Format(T("L.S.alerts.hub.historyFolder", "The history is kept in {0}"), AppPaths.HistoryDir)));
        var openHistory = new Button { Content = T("L.Tray.AlertHistory", "Alert history…") };
        openHistory.Click += (_, _) => _host.OpenAlertHistory();
        page.Children.Add(Ui.Row(T("L.S.alerts.hub.historyRow", "Alert history"), openHistory,
            T("L.S.alerts.hub.historyRow.Desc", "Every alert that was kept, newest first. Double-click one to switch to its client.")));
        return page;
    }
}
