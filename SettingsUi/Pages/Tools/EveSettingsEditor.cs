using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Tools › EVE settings (spec §5): the two folders, the mode switch (whole profile, one character, one
/// account) and each mode's pickers and buttons. The rules and the file work are EveSettingsModel's; this only lays
/// them out. Nothing is written until the player presses a button.</summary>
public sealed class EveSettingsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly TextBox _settingsFolder = ReadOnlyBox("L.S.Eve.SettingsFolder");
    private readonly TextBox _backupFolder = ReadOnlyBox("L.S.Eve.BackupFolder");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Dictionary<EveCopyMode, RadioButton> _modes = new();
    private readonly Dictionary<EveCopyMode, Border> _panels = new();
    private readonly Border _namesCard;
    private readonly StackPanel _profileSources = new(), _profileTargets = new();
    private readonly HashSet<string> _tickedProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _profileGroup = "EveProfile" + Guid.NewGuid().ToString("N");
    private string? _sourceProfilePath;
    private readonly EntryPicker _charFrom, _charTo, _accountFrom, _accountTo;
    private readonly CheckBox _charToAll = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox _accountToAll = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _accountName = new() { MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _fetch;
    private bool _loading;

    public EveSettingsEditor(SettingsContext ctx, EveSettingsModel? model = null)
    {
        _ctx = ctx;
        Model = model ?? new EveSettingsModel(ctx);
        _charFrom = new EntryPicker("L.S.Eve.Source", true, p => Model.Characters(p));
        _charTo = new EntryPicker("L.S.Eve.Targets", false, p => Model.Characters(p));
        _accountFrom = new EntryPicker("L.S.Eve.Source", true, p => Model.Accounts(p));
        _accountTo = new EntryPicker("L.S.Eve.Targets", false, p => Model.Accounts(p));
        Loc.SetKey(_charToAll, "L.S.Eve.ToAllCharacters");
        Loc.SetKey(_accountToAll, "L.S.Eve.ToAllAccounts");
        AutomationProperties.SetName(_accountName, Str("L.S.Eve.Label"));
        _fetch = EditorCard.Button("L.S.Eve.FetchNames", FetchNames);

        var root = new StackPanel();
        root.Children.Add(FoldersCard());
        root.Children.Add(ModeCard());
        root.Children.Add(Panel(EveCopyMode.Profile, ProfileContent()));
        root.Children.Add(Panel(EveCopyMode.Character, CharacterContent()));
        root.Children.Add(Panel(EveCopyMode.Account, AccountContent()));
        var names = new StackPanel();
        names.Children.Add(_fetch);
        _fetch.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        names.Children.Add(Wrapped(EditorCard.Note("L.S.Eve.FetchNote")));
        _namesCard = EditorCard.Frame(names);
        root.Children.Add(_namesCard);
        Content = root;
        SetMode(EveCopyMode.Profile);
        Refresh();
    }

    public EveSettingsModel Model { get; }

    /// <summary>Session-only, as the old mode buttons were.</summary>
    public EveCopyMode Mode { get; private set; }

    public bool IsShown(EveCopyMode mode) => _panels[mode].Visibility == Visibility.Visible;

    /// <summary>The character numbers listed under "Copy from" (tests).</summary>
    public IReadOnlyList<string> CharacterSourceIds => _charFrom.Entries.Select(e => e.Id).ToList();

    private static string Str(string key) => SettingsContext.Str(key);

    // ── Layout ──

    private static TextBox ReadOnlyBox(string nameKey)
    {
        var box = new TextBox { IsReadOnly = true, MinWidth = 320, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, Str(nameKey));
        return box;
    }

    private static TextBlock Wrapped(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 4, 0, 8);
        return text;
    }

    private UIElement FoldersCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(EditorCard.Field("L.S.Eve.SettingsFolder", EditorCard.Row(_settingsFolder,
            EditorCard.Button("L.S.Shell.Browse", BrowseSettings), EditorCard.Button("L.S.Eve.Find", FindSettings))));
        stack.Children.Add(EditorCard.Field("L.S.Eve.BackupFolder", EditorCard.Row(_backupFolder,
            EditorCard.Button("L.S.Shell.Browse", BrowseBackup), EditorCard.Button("L.S.Eve.StandardBackup", StandardBackup))));
        var refresh = EditorCard.Button("L.S.Eve.Refresh", Refresh);
        refresh.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        stack.Children.Add(refresh);
        stack.Children.Add(_status);
        return EditorCard.Build("tools.eve.editor", null, stack);
    }

    private UIElement ModeCard()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var group = "EveMode" + Guid.NewGuid().ToString("N");
        foreach (var (mode, key) in new[]
        {
            (EveCopyMode.Profile, "L.S.Eve.ModeProfile"), (EveCopyMode.Character, "L.S.Eve.ModeCharacter"), (EveCopyMode.Account, "L.S.Eve.ModeAccount"),
        })
        {
            var radio = new RadioButton { GroupName = group, Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
            Loc.SetKey(radio, key);
            var m = mode;
            radio.Checked += (_, _) => { if (!_loading) SetMode(m); };
            _modes[mode] = radio;
            row.Children.Add(radio);
        }
        var stack = new StackPanel();
        stack.Children.Add(EditorCard.Note("L.S.Eve.ModeTitle", strong: true));
        stack.Children.Add(row);
        return EditorCard.Frame(stack);
    }

    private Border Panel(EveCopyMode mode, UIElement content)
    {
        var frame = EditorCard.Frame(content);
        _panels[mode] = frame;
        return frame;
    }

    private static UIElement Column(string titleKey, UIElement list)
    {
        var column = new StackPanel { MinWidth = 260, Margin = new Thickness(0, 8, 24, 8) };
        column.Children.Add(EditorCard.Note(titleKey, strong: true));
        column.Children.Add(new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return column;
    }

    private UIElement ProfileContent()
    {
        var lists = new WrapPanel();
        lists.Children.Add(Column("L.S.Eve.Source", _profileSources));
        lists.Children.Add(Column("L.S.Eve.Targets", _profileTargets));
        var stack = new StackPanel();
        stack.Children.Add(Wrapped(EditorCard.Note("L.S.Eve.ModeProfileDesc")));
        stack.Children.Add(lists);
        stack.Children.Add(EditorCard.Row(
            EditorCard.Button("L.S.Eve.BackUp", () => Model.BackUpInteractive(TickedProfiles())),
            EditorCard.Button("L.S.Eve.CopyProfile", CopyProfiles)));
        return stack;
    }

    private UIElement CharacterContent()
    {
        var lists = new WrapPanel();
        lists.Children.Add(_charFrom.View);
        lists.Children.Add(_charTo.View);
        var stack = new StackPanel();
        stack.Children.Add(Wrapped(EditorCard.Note("L.S.Eve.ModeCharacterDesc")));
        stack.Children.Add(lists);
        stack.Children.Add(_charToAll);
        stack.Children.Add(EditorCard.Row(EditorCard.Button("L.S.Eve.CopyCharacter", () =>
            Model.CopyCharacterInteractive(_charFrom.Profile, _charFrom.Chosen, _charTo.Profile, _charTo.Ticked.ToList(), _charToAll.IsChecked == true))));
        return stack;
    }

    private UIElement AccountContent()
    {
        var lists = new WrapPanel();
        lists.Children.Add(_accountFrom.View);
        lists.Children.Add(_accountTo.View);
        var stack = new StackPanel();
        stack.Children.Add(Wrapped(EditorCard.Note("L.S.Eve.ModeAccountDesc")));
        stack.Children.Add(lists);
        stack.Children.Add(_accountToAll);
        stack.Children.Add(EditorCard.Row(
            EditorCard.Button("L.S.Eve.CopyAccount", () =>
                Model.CopyAccountInteractive(_accountFrom.Profile, _accountFrom.Chosen, _accountTo.Profile, _accountTo.Ticked.ToList(), _accountToAll.IsChecked == true)),
            EditorCard.Button("L.S.Eve.Detect", () => { Model.DetectInteractive(); ReloadPickers(); })));
        var naming = EditorCard.Field("L.S.Eve.Label", EditorCard.Row(_accountName, EditorCard.Button("L.S.Eve.SetLabel", SetAccountName)));
        naming.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(naming);
        return stack;
    }

    // ── State ──

    public void SetMode(EveCopyMode mode)
    {
        Mode = mode;
        _loading = true;
        _modes[mode].IsChecked = true;
        _loading = false;
        foreach (var (m, panel) in _panels) panel.Visibility = m == mode ? Visibility.Visible : Visibility.Collapsed;
        _namesCard.Visibility = mode == EveCopyMode.Profile ? Visibility.Collapsed : Visibility.Visible;
        LoadPickers();   // names load the first time a character or account list is drawn
    }

    /// <summary>Re-reads the folders and the profiles (a page refresh, Refresh, a folder change).</summary>
    public void Refresh()
    {
        Model.Refresh();
        var dir = Model.EveDir;
        _settingsFolder.Text = dir.Length > 0 ? dir : _ctx.S.EveSettingsDir;
        _backupFolder.Text = Model.BackupRoot;
        _status.Text = Model.StatusText();
        _status.SetResourceReference(TextBlock.ForegroundProperty, Model.EveRunning ? "Fx.WarningBrush" : "Fx.TextSecondaryBrush");
        DrawProfiles();
        LoadPickers();
    }

    private void LoadPickers()
    {
        if (Mode == EveCopyMode.Character) { _charFrom.SetProfiles(Model.Profiles, 0); _charTo.SetProfiles(Model.Profiles, 1); }
        if (Mode == EveCopyMode.Account) { _accountFrom.SetProfiles(Model.Profiles, 0); _accountTo.SetProfiles(Model.Profiles, 1); }
    }

    private void ReloadPickers()
    {
        _charFrom.Reload();
        _charTo.Reload();
        _accountFrom.Reload();
        _accountTo.Reload();
    }

    private void DrawProfiles()
    {
        var profiles = Model.Profiles;
        var paths = profiles.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_sourceProfilePath != null && !paths.Contains(_sourceProfilePath)) _sourceProfilePath = null;
        _tickedProfiles.IntersectWith(paths);
        _profileSources.Children.Clear();
        _profileTargets.Children.Clear();
        if (profiles.Count == 0)
        {
            _profileSources.Children.Add(EditorCard.Note("L.S.Eve.EmptyList"));
            _profileTargets.Children.Add(EditorCard.Note("L.S.Eve.EmptyList"));
            return;
        }
        foreach (var p in profiles)
        {
            var path = p.Path;
            var label = string.Format(Str("L.S.Eve.ProfileItem"), p.Name, p.Accounts);
            var source = new RadioButton
            {
                Content = label, GroupName = _profileGroup, Margin = new Thickness(0, 2, 0, 2),
                IsChecked = string.Equals(path, _sourceProfilePath, StringComparison.OrdinalIgnoreCase),
            };
            source.Checked += (_, _) => _sourceProfilePath = path;
            _profileSources.Children.Add(source);
            var target = new CheckBox { Content = label, Margin = new Thickness(0, 2, 0, 2), IsChecked = _tickedProfiles.Contains(path) };
            target.Checked += (_, _) => _tickedProfiles.Add(path);
            target.Unchecked += (_, _) => _tickedProfiles.Remove(path);
            _profileTargets.Children.Add(target);
        }
    }

    private IReadOnlyList<EveProfile> TickedProfiles() => Model.Profiles.Where(p => _tickedProfiles.Contains(p.Path)).ToList();

    private EveProfile? SourceProfile() =>
        Model.Profiles.FirstOrDefault(p => string.Equals(p.Path, _sourceProfilePath, StringComparison.OrdinalIgnoreCase));

    // ── Buttons ──

    private void CopyProfiles()
    {
        Model.CopyProfileInteractive(SourceProfile(), TickedProfiles());
        Refresh();
    }

    private void BrowseSettings()
    {
        var picked = _ctx.Host.PickFolder(_settingsFolder.Text);
        if (picked == null) return;
        Model.SetSettingsFolder(picked);
        Refresh();
    }

    private void FindSettings()
    {
        if (!Model.FindSettingsFolder()) _ctx.Host.Toast(Str("L.S.Eve.NotFound"));
        Refresh();
    }

    private void BrowseBackup()
    {
        var picked = _ctx.Host.PickFolder(Model.BackupRoot);
        if (picked == null) return;
        Model.SetBackupFolder(picked);
        _backupFolder.Text = Model.BackupRoot;
    }

    private void StandardBackup()
    {
        Model.ResetBackupFolder();
        _backupFolder.Text = Model.BackupRoot;
    }

    private void SetAccountName()
    {
        if (!Model.SetLabelInteractive(_accountFrom.Chosen, _accountName.Text)) return;
        _accountName.Text = "";
        _accountFrom.Reload();
        _accountTo.Reload();
    }

    /// <summary>The one network call on this page (EveManagerService.EnrichWithESI, through the model).</summary>
    private async void FetchNames()
    {
        _fetch.IsEnabled = false;
        try
        {
            await Model.FetchNamesInteractive();
            ReloadPickers();
        }
        catch (Exception)
        {
            _ctx.Host.Toast(Str("L.S.Eve.FetchFailed"));   // an async void must never let one escape
        }
        finally { _fetch.IsEnabled = true; }
    }

    /// <summary>One side of a character or account copy: its profile, a search box and the profile's entries, one
    /// choice for "Copy from" (radio buttons) or ticks for "Copy to".</summary>
    private sealed class EntryPicker
    {
        private readonly bool _single;
        private readonly Func<EveProfile, IReadOnlyList<EveEntry>> _list;
        private readonly ComboBox _profile = new() { MinWidth = 220 };
        private readonly TextBox _search = new() { MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly StackPanel _rows = new();
        private readonly string _group = "EveEntry" + Guid.NewGuid().ToString("N");
        private IReadOnlyList<EveProfile> _profiles = Array.Empty<EveProfile>();
        private bool _loading;

        public EntryPicker(string titleKey, bool single, Func<EveProfile, IReadOnlyList<EveEntry>> list)
        {
            _single = single;
            _list = list;
            AutomationProperties.SetName(_profile, Str(titleKey) + " " + Str("L.S.Eve.Profile"));
            AutomationProperties.SetName(_search, Str(titleKey) + " " + Str("L.S.Eve.Search"));
            _profile.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                Chosen = null;
                Ticked.Clear();
                Reload();
            };
            _search.TextChanged += (_, _) => Draw();
            var panel = new StackPanel { MinWidth = 280, Margin = new Thickness(0, 8, 24, 8) };
            panel.Children.Add(EditorCard.Note(titleKey, strong: true));
            panel.Children.Add(EditorCard.Field("L.S.Eve.Profile", _profile));
            panel.Children.Add(EditorCard.Field("L.S.Eve.Search", _search));
            panel.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            View = panel;
        }

        public UIElement View { get; }
        public IReadOnlyList<EveEntry> Entries { get; private set; } = Array.Empty<EveEntry>();
        public string? Chosen { get; private set; }
        public HashSet<string> Ticked { get; } = new();

        public EveProfile? Profile =>
            _profile.SelectedIndex >= 0 && _profile.SelectedIndex < _profiles.Count ? _profiles[_profile.SelectedIndex] : null;

        /// <summary>A new profile list: keeps the chosen profile when it is still there, else <paramref name="fallback"/>
        /// (the old dropdowns: the first profile copies to the second).</summary>
        public void SetProfiles(IReadOnlyList<EveProfile> profiles, int fallback)
        {
            var keep = Profile?.Path;
            _profiles = profiles;
            _loading = true;
            _profile.Items.Clear();
            foreach (var p in profiles) _profile.Items.Add(p.Name);
            var index = keep == null ? -1 : profiles.ToList().FindIndex(p => string.Equals(p.Path, keep, StringComparison.OrdinalIgnoreCase));
            _profile.SelectedIndex = index >= 0 ? index : profiles.Count == 0 ? -1 : Math.Min(fallback, profiles.Count - 1);
            _loading = false;
            Reload();
        }

        /// <summary>Re-reads the entries (a new name or label); a choice or tick that is gone is dropped.</summary>
        public void Reload()
        {
            Entries = Profile is { } p ? _list(p) : Array.Empty<EveEntry>();
            var ids = Entries.Select(e => e.Id).ToHashSet();
            if (Chosen != null && !ids.Contains(Chosen)) Chosen = null;
            Ticked.IntersectWith(ids);
            Draw();
        }

        private void Draw()
        {
            _rows.Children.Clear();
            var shown = EveSettingsModel.Filter(Entries, _search.Text);
            if (shown.Count == 0) { _rows.Children.Add(EditorCard.Note("L.S.Eve.EmptyList")); return; }
            foreach (var e in shown)
            {
                var id = e.Id;
                if (_single)
                {
                    var radio = new RadioButton { Content = e.Label, GroupName = _group, IsChecked = id == Chosen, Margin = new Thickness(0, 2, 0, 2) };
                    radio.Checked += (_, _) => Chosen = id;
                    _rows.Children.Add(radio);
                }
                else
                {
                    var tick = new CheckBox { Content = e.Label, IsChecked = Ticked.Contains(id), Margin = new Thickness(0, 2, 0, 2) };
                    tick.Checked += (_, _) => Ticked.Add(id);
                    tick.Unchecked += (_, _) => Ticked.Remove(id);
                    _rows.Children.Add(tick);
                }
            }
        }
    }
}
