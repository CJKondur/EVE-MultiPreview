using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S8: Tools › EVE settings, the Avalonia port of EveSettingsEditor (keep in sync) over the shared EveSettingsModel:
/// the two folders, the mode switch (whole profile, one character, one account) and each mode's pickers and buttons. The
/// rules, the questions and the file work are the model's (EveManagerService, each replaced file backed up first); on Linux
/// the model finds EVE's settings inside the Proton prefix (EveLogs.FindEveDir), reads names from the Proton chat logs and
/// asks the app whether a client runs. Nothing is written until the player presses a button; the one network call is
/// Fetch missing names (ESI's public name lookup, character numbers only), on its click.</summary>
internal sealed partial class SettingsEditors
{
    internal Control EveSettingsCard(SettingsContext ctx)
    {
        var model = new EveSettingsModel(ctx, () => _host.EveRunning,
            s => Services.EveManagerService.LoadCharNameCache(EveLogs.Folders(s).Chat, EveSettingsModel.CachedNames(s)),
            findEveDir: EveLogs.FindEveDir);
        var mode = EveCopyMode.Profile;
        var root = new StackPanel { Spacing = 12 };
        // Everything the local functions at the end read, made first (the buttons' lambdas call them).
        var sources = new StackPanel();
        var targets = new StackPanel();
        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? sourcePath = null;
        string profileGroup = "EveProfile" + Guid.NewGuid().ToString("N");
        var charFrom = new EvePicker("L.S.Eve.Source", true, p => model.Characters(p));
        var charTo = new EvePicker("L.S.Eve.Targets", false, p => model.Characters(p));
        var accountFrom = new EvePicker("L.S.Eve.Source", true, p => model.Accounts(p));
        var accountTo = new EvePicker("L.S.Eve.Targets", false, p => model.Accounts(p));
        var names = new StackPanel();

        // ── Folders ──
        var folders = Titled("tools.eve.editor");
        var settingsBox = new TextBox { IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        var backupBox = new TextBox { IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        folders.Children.Add(FolderField("L.S.Eve.SettingsFolder", settingsBox,
            Action("L.S.Shell.Browse", () =>
            {
                if (ctx.Host.PickFolder(settingsBox.Text) is not { } picked) return;
                model.SetSettingsFolder(picked);
                Refresh();
            }),
            Action("L.S.Eve.Find", () =>
            {
                if (!model.FindSettingsFolder()) ctx.Host.Toast(L("L.S.Eve.NotFound"));
                Refresh();
            })));
        folders.Children.Add(FolderField("L.S.Eve.BackupFolder", backupBox,
            Action("L.S.Shell.Browse", () =>
            {
                if (ctx.Host.PickFolder(model.BackupRoot) is not { } picked) return;
                model.SetBackupFolder(picked);
                backupBox.Text = model.BackupRoot;
            }),
            Action("L.S.Eve.StandardBackup", () =>
            {
                model.ResetBackupFolder();
                backupBox.Text = model.BackupRoot;
            })));
        var refresh = Action("L.S.Eve.Refresh", () => Refresh());
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        folders.Children.Add(refresh);
        folders.Children.Add(status);
        root.Children.Add(folders);

        // ── The mode switch ──
        var modes = new Dictionary<EveCopyMode, RadioButton>();
        var panels = new Dictionary<EveCopyMode, Control>();
        var modeRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        string modeGroup = "EveMode" + Guid.NewGuid().ToString("N");
        foreach (var (m, key) in new[] { (EveCopyMode.Profile, "L.S.Eve.ModeProfile"), (EveCopyMode.Character, "L.S.Eve.ModeCharacter"), (EveCopyMode.Account, "L.S.Eve.ModeAccount") })
        {
            var radio = new RadioButton { GroupName = modeGroup, Content = new TextBlock { Text = Ui.NoAccess(S3(key)) }, Margin = new Thickness(0, 0, 24, 0) };
            var chosen = m;
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) SetMode(chosen); };
            modes[m] = radio;
            modeRow.Children.Add(radio);
        }
        var modeCard = new StackPanel();
        modeCard.Children.Add(Strong("L.S.Eve.ModeTitle"));
        modeCard.Children.Add(modeRow);
        root.Children.Add(modeCard);

        // ── Whole profile ──
        IReadOnlyList<EveProfile> Ticked() => model.Profiles.Where(p => ticked.Contains(p.Path)).ToList();
        EveProfile? Source() => model.Profiles.FirstOrDefault(p => string.Equals(p.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
        var profilePanel = new StackPanel();
        profilePanel.Children.Add(Wrapped("L.S.Eve.ModeProfileDesc"));
        profilePanel.Children.Add(Columns(Column("L.S.Eve.Source", sources), Column("L.S.Eve.Targets", targets)));
        profilePanel.Children.Add(Buttons(
            Action("L.S.Eve.BackUp", () => model.BackUpInteractive(Ticked())),
            Action("L.S.Eve.CopyProfile", () => { model.CopyProfileInteractive(Source(), Ticked()); Refresh(); })));
        panels[EveCopyMode.Profile] = profilePanel;
        root.Children.Add(profilePanel);

        // ── One character ──
        var charToAll = new CheckBox { Content = new TextBlock { Text = Ui.NoAccess(S3("L.S.Eve.ToAllCharacters")) }, Margin = new Thickness(0, 8) };
        var charPanel = new StackPanel();
        charPanel.Children.Add(Wrapped("L.S.Eve.ModeCharacterDesc"));
        charPanel.Children.Add(Columns(charFrom.View, charTo.View));
        charPanel.Children.Add(charToAll);
        // EVE keeps the overview and window layout per account: copied with the character unless this is cleared.
        var charWithAccount = new CheckBox { Content = new TextBlock { Text = Ui.NoAccess(S3("L.S.Eve.WithAccount")) }, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        charPanel.Children.Add(charWithAccount);
        charPanel.Children.Add(Buttons(Action("L.S.Eve.CopyCharacter", () =>
            model.CopyCharacterInteractive(charFrom.Profile, charFrom.Chosen, charTo.Profile, charTo.Ticked.ToList(), charToAll.IsChecked == true,
                charWithAccount.IsChecked == true))));
        panels[EveCopyMode.Character] = charPanel;
        root.Children.Add(charPanel);

        // ── One account ──
        var accountToAll = new CheckBox { Content = new TextBlock { Text = Ui.NoAccess(S3("L.S.Eve.ToAllAccounts")) }, Margin = new Thickness(0, 8) };
        var accountName = new TextBox { MinWidth = 220 };
        var accountPanel = new StackPanel();
        accountPanel.Children.Add(Wrapped("L.S.Eve.ModeAccountDesc"));
        accountPanel.Children.Add(Columns(accountFrom.View, accountTo.View));
        accountPanel.Children.Add(accountToAll);
        accountPanel.Children.Add(Buttons(
            Action("L.S.Eve.CopyAccount", () =>
                model.CopyAccountInteractive(accountFrom.Profile, accountFrom.Chosen, accountTo.Profile, accountTo.Ticked.ToList(), accountToAll.IsChecked == true)),
            Action("L.S.Eve.Detect", () => { model.DetectInteractive(); ReloadPickers(); })));
        var naming = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        naming.Children.Add(LinuxLooks.Text(Ui.NoAccess(S3("L.S.Eve.Label")), "Label"));
        naming.Children.Add(Buttons(accountName, Action("L.S.Eve.SetLabel", () =>
        {
            if (!model.SetLabelInteractive(accountFrom.Chosen, accountName.Text)) return;
            accountName.Text = "";
            accountFrom.Reload();
            accountTo.Reload();
        })));
        accountPanel.Children.Add(naming);
        panels[EveCopyMode.Account] = accountPanel;
        root.Children.Add(accountPanel);

        // ── Names from ESI (the character and account modes) ──
        Button fetch = null!;
        fetch = Action("L.S.Eve.FetchNames", async () =>
        {
            fetch.IsEnabled = false;
            try
            {
                await model.FetchNamesInteractive();
                ReloadPickers();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ctx.Host.Toast(S3("L.S.Eve.FetchFailed")); }   // an async void must never let one escape
            finally { fetch.IsEnabled = true; }
        });
        fetch.HorizontalAlignment = HorizontalAlignment.Left;
        names.Children.Add(fetch);
        names.Children.Add(Wrapped("L.S.Eve.FetchNote"));
        root.Children.Add(names);

        void ReloadPickers()
        {
            charFrom.Reload();
            charTo.Reload();
            accountFrom.Reload();
            accountTo.Reload();
        }

        void LoadPickers()
        {
            if (mode == EveCopyMode.Character) { charFrom.SetProfiles(model.Profiles, 0); charTo.SetProfiles(model.Profiles, 1); }
            if (mode == EveCopyMode.Account) { accountFrom.SetProfiles(model.Profiles, 0); accountTo.SetProfiles(model.Profiles, 1); }
        }

        void SetMode(EveCopyMode m)
        {
            mode = m;
            if (modes[m].IsChecked != true) modes[m].IsChecked = true;
            foreach (var (k, panel) in panels) panel.IsVisible = k == m;
            names.IsVisible = m != EveCopyMode.Profile;
            LoadPickers();   // names load the first time a character or account list is drawn
        }

        void DrawProfiles()
        {
            var profiles = model.Profiles;
            var paths = profiles.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (sourcePath != null && !paths.Contains(sourcePath)) sourcePath = null;
            ticked.IntersectWith(paths);
            sources.Children.Clear();
            targets.Children.Clear();
            if (profiles.Count == 0)
            {
                sources.Children.Add(Wrapped("L.S.Eve.EmptyList"));
                targets.Children.Add(Wrapped("L.S.Eve.EmptyList"));
                return;
            }
            foreach (var p in profiles)
            {
                var path = p.Path;
                var label = string.Format(S3("L.S.Eve.ProfileItem"), p.Name, p.Accounts);
                var source = new RadioButton { GroupName = profileGroup, Content = new TextBlock { Text = label }, Margin = new Thickness(0, 2),
                    IsChecked = string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase) };
                source.IsCheckedChanged += (_, _) => { if (source.IsChecked == true) sourcePath = path; };
                sources.Children.Add(source);
                var target = new CheckBox { Content = new TextBlock { Text = label }, Margin = new Thickness(0, 2), IsChecked = ticked.Contains(path) };
                target.IsCheckedChanged += (_, _) => { if (target.IsChecked == true) ticked.Add(path); else ticked.Remove(path); };
                targets.Children.Add(target);
            }
        }

        void Refresh()
        {
            model.Refresh();
            var dir = model.EveDir;
            settingsBox.Text = dir.Length > 0 ? dir : ctx.S.EveSettingsDir;
            backupBox.Text = model.BackupRoot;
            // The model's "not found" names the Windows folder: the Linux words say where Proton keeps it.
            var said = model.StatusText();
            status.Text = said == S3("L.S.Eve.NotFound") ? L("L.S.Eve.NotFound") : said;
            LinuxLooks.Paint(status, TextBlock.ForegroundProperty, model.EveRunning ? "Fx.WarningBrush" : "Fx.TextSecondaryBrush");
            DrawProfiles();
            LoadPickers();
        }

        SetMode(EveCopyMode.Profile);
        Refresh();
        return root;
    }

    // ── The EVE settings card's pieces ──

    private static TextBlock Strong(string key)
    {
        var t = LinuxLooks.Text(Ui.NoAccess(S3(key)), "Label");
        t.FontWeight = FontWeight.SemiBold;
        return t;
    }

    private static TextBlock Wrapped(string key)
    {
        var t = LinuxLooks.Text(S3(key), "Desc");
        t.Margin = new Thickness(0, 4, 0, 8);
        return t;
    }

    /// <summary>A folder: its label, the path across the whole card, then its buttons (they wrap when a translation is long).</summary>
    private static Control FolderField(string labelKey, TextBox box, params Button[] buttons)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(LinuxLooks.Text(Ui.NoAccess(S3(labelKey)), "Label"));
        box.Margin = new Thickness(0, 4);
        stack.Children.Add(box);
        stack.Children.Add(Buttons(buttons));
        return stack;
    }

    /// <summary>Two columns that share the width.</summary>
    private static Grid Columns(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static Control Column(string titleKey, Control list)
    {
        var column = new StackPanel { MinWidth = 260, Margin = new Thickness(0, 8, 24, 8) };
        column.Children.Add(Strong(titleKey));
        column.Children.Add(new ScrollViewer { Content = list, MaxHeight = EvePicker.ListHeight, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        return column;
    }

    /// <summary>One side of a character or account copy (EveSettingsEditor.EntryPicker): its profile, a search box and the
    /// profile's entries, one choice for "Copy from" (radio buttons) or ticks for "Copy to".</summary>
    private sealed class EvePicker
    {
        public const double ListHeight = 240;
        private readonly bool _single;
        private readonly Func<EveProfile, IReadOnlyList<EveEntry>> _list;
        private readonly ComboBox _profile = new() { MinWidth = 220 };
        private readonly TextBox _search = new() { MinWidth = 220 };
        private readonly StackPanel _rows = new();
        private readonly string _group = "EveEntry" + Guid.NewGuid().ToString("N");
        private IReadOnlyList<EveProfile> _profiles = Array.Empty<EveProfile>();
        private bool _loading;

        public EvePicker(string titleKey, bool single, Func<EveProfile, IReadOnlyList<EveEntry>> list)
        {
            _single = single;
            _list = list;
            _profile.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                Chosen = null;
                Ticked.Clear();
                Reload();
            };
            _search.TextChanged += (_, _) => Draw();
            var panel = new StackPanel { MinWidth = 280, Margin = new Thickness(0, 8, 24, 8), Spacing = 4 };
            panel.Children.Add(Strong(titleKey));
            panel.Children.Add(LinuxLooks.Text(Ui.NoAccess(S3("L.S.Eve.Profile")), "Desc"));
            panel.Children.Add(_profile);
            panel.Children.Add(LinuxLooks.Text(Ui.NoAccess(S3("L.S.Eve.Search")), "Desc"));
            panel.Children.Add(_search);
            panel.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = ListHeight, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            View = panel;
        }

        public Control View { get; }
        public string? Chosen { get; private set; }
        public HashSet<string> Ticked { get; } = new();
        private IReadOnlyList<EveEntry> _entries = Array.Empty<EveEntry>();

        public EveProfile? Profile =>
            _profile.SelectedIndex >= 0 && _profile.SelectedIndex < _profiles.Count ? _profiles[_profile.SelectedIndex] : null;

        /// <summary>A new profile list: keeps the chosen profile when it is still there, else <paramref name="fallback"/>.</summary>
        public void SetProfiles(IReadOnlyList<EveProfile> profiles, int fallback)
        {
            var keep = Profile?.Path;
            _profiles = profiles;
            _loading = true;
            _profile.Items.Clear();
            foreach (var p in profiles) _profile.Items.Add(new TextBlock { Text = p.Name });
            var index = keep == null ? -1 : profiles.ToList().FindIndex(p => string.Equals(p.Path, keep, StringComparison.OrdinalIgnoreCase));
            _profile.SelectedIndex = index >= 0 ? index : profiles.Count == 0 ? -1 : Math.Min(fallback, profiles.Count - 1);
            _loading = false;
            Reload();
        }

        /// <summary>Re-reads the entries (a new name or label); a choice or tick that is gone is dropped.</summary>
        public void Reload()
        {
            _entries = Profile is { } p ? _list(p) : Array.Empty<EveEntry>();
            var ids = _entries.Select(e => e.Id).ToHashSet();
            if (Chosen != null && !ids.Contains(Chosen)) Chosen = null;
            Ticked.IntersectWith(ids);
            Draw();
        }

        private void Draw()
        {
            _rows.Children.Clear();
            var shown = EveSettingsModel.Filter(_entries, _search.Text);
            if (shown.Count == 0) { _rows.Children.Add(Wrapped("L.S.Eve.EmptyList")); return; }
            foreach (var e in shown)
            {
                var id = e.Id;
                if (_single)
                {
                    var radio = new RadioButton { Content = new TextBlock { Text = e.Label }, GroupName = _group, IsChecked = id == Chosen, Margin = new Thickness(0, 2) };
                    radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) Chosen = id; };
                    _rows.Children.Add(radio);
                }
                else
                {
                    var tick = new CheckBox { Content = new TextBlock { Text = e.Label }, IsChecked = Ticked.Contains(id), Margin = new Thickness(0, 2) };
                    tick.IsCheckedChanged += (_, _) => { if (tick.IsChecked == true) Ticked.Add(id); else Ticked.Remove(id); };
                    _rows.Children.Add(tick);
                }
            }
        }
    }
}
