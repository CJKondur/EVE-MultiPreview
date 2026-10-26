using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › General › Other programs › Commands for buttons (IDEAS #15, Plan 38): one primary button, "Make Stream
/// Deck buttons", that writes a folder of ready shortcuts and opens it, after one plain question the first time (it turns the
/// command switches on); and, while commands are on, a list of every command a button can run (the fixed ones, one per
/// running character, group and profile), its command line, and three buttons: copy the line, save it as a Windows shortcut, and
/// try it (send it to this MultiPreview the way a button would, and say what came of it). The only settings it changes are
/// the command switches, through the normal path (set, save, follow). A player's name is only ever an argument of
/// string.Format here, never its format.</summary>
public sealed class CommandButtonsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "app.general.buttons";
    private readonly SettingsContext _ctx;
    private readonly Func<string?> _exePath;
    private readonly Func<string, bool> _copy;
    private readonly Func<string, string, string, string?, bool> _saveShortcut;
    private readonly Func<RemoteCommand, Task<RemoteOutcome>> _send;
    private readonly Func<bool> _elevated;
    private readonly Func<string, IReadOnlyList<ButtonSpec>, ButtonFolderResult> _writeFolder;
    private readonly Action<string> _openFolder;
    private readonly Action<Action> _afterQueuedInput;
    private readonly ComboBox _pick = new() { MinWidth = 360 };
    private readonly TextBox _line = new() { IsReadOnly = true, MinWidth = 360 };
    private readonly UIElement _noProgram = EditorCard.Note("L.S.Cmd.NoProgram");
    private readonly StackPanel _controls = new();
    private readonly Button _make;
    private bool _busy;

    /// <summary>The real seams: this program's path, the clipboard, the shortcut writer, the pipe (asynchronously: a
    /// window's own thread never waits for the answer), the folder writer and Explorer.</summary>
    public CommandButtonsEditor(SettingsContext ctx) : this(ctx, () => CommandLines.ButtonProgram(Environment.ProcessPath), ClipboardText.Set, ShortcutFile.Create,
        command => CommandPipe.SendAsync(CommandPipe.CurrentName(), command, CommandPipe.SendTime))
    {
    }

    /// <param name="elevated">Whether this MultiPreview runs as administrator; the real answer unless a test hands one in.</param>
    /// <param name="writeFolder">Folder, buttons: writes the folder (real: <see cref="StreamDeckButtons.Write"/> with the real
    /// seams, on this thread, which is the window's STA thread) and says how many shortcuts it wrote and could not.</param>
    /// <param name="openFolder">Shows a folder to the player (real: Explorer, the path as one argument).</param>
    /// <param name="afterQueuedInput">Runs an action once the input already queued has been handled (real: the dispatcher at
    /// Background priority, below input), so a second click of a double-click, which WPF delivers only after the first click's
    /// handler has returned, finds the button still off.</param>
    public CommandButtonsEditor(SettingsContext ctx, Func<string?> exePath, Func<string, bool> copy,
        Func<string, string, string, string?, bool> saveShortcut, Func<RemoteCommand, Task<RemoteOutcome>> send, Func<bool>? elevated = null,
        Func<string, IReadOnlyList<ButtonSpec>, ButtonFolderResult>? writeFolder = null, Action<string>? openFolder = null,
        Action<Action>? afterQueuedInput = null)
    {
        _ctx = ctx;
        _exePath = exePath;
        _copy = copy;
        _saveShortcut = saveShortcut;
        _send = send;
        _elevated = elevated ?? CommandPipe.IsElevated;
        _writeFolder = writeFolder ?? ((folder, specs) => StreamDeckButtons.Write(folder, _exePath() ?? "", specs, StreamDeckButtons.RealSeams()));
        _openFolder = openFolder ?? DiagnosticsService.OpenFolder;
        _afterQueuedInput = afterQueuedInput ?? (action => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, action));
        _pick.SelectionChanged += (_, _) => _line.Text = Line;
        EditorCard.NameFrom(_line, "L.S." + ItemId);
        _controls.Children.Add(EditorCard.Field("L.S.Cmd.Pick", _pick));
        _controls.Children.Add(_line);
        var buttons = EditorCard.Row(EditorCard.Button("L.S.Cmd.Copy", Copy), EditorCard.Button("L.S.Cmd.Shortcut", SaveShortcut),
            EditorCard.Button("L.S.Cmd.TryIt", () => _ = TryIt()));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        _controls.Children.Add(buttons);
        _make = EditorCard.Button("L.S.Cmd.MakeButtons", MakeButtons);
        _make.SetResourceReference(StyleProperty, "Fx.PrimaryButton");   // the one thing most players do here
        _make.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var makeDesc = EditorCard.Note("L.S.Cmd.MakeButtons.Desc");
        makeDesc.Margin = new Thickness(0, 4, 0, 12);
        var below = new StackPanel();
        below.Children.Add(_make);
        below.Children.Add(makeDesc);
        below.Children.Add(_noProgram);
        below.Children.Add(_controls);
        Content = EditorCard.Build(ItemId, null, below);
        Refresh();
    }

    /// <summary>Every command with a line, as the list shows it. Empty without a program path.</summary>
    public IReadOnlyList<CommandChoice> Choices { get; private set; } = Array.Empty<CommandChoice>();

    /// <summary>The command picked in the list; null while none is.</summary>
    public CommandChoice? Selected => _pick.SelectedIndex >= 0 && _pick.SelectedIndex < Choices.Count ? Choices[_pick.SelectedIndex] : null;

    /// <summary>The picked command's line; empty when none is picked.</summary>
    public string Line => Selected?.Line ?? "";

    /// <summary>Rebuilds the list from what runs and is saved now, and keeps the picked command when it is still there.</summary>
    public void Refresh()
    {
        var keep = Selected?.Command;
        Choices = CommandLines.Choices(_exePath(), CharacterDirectory.Running(_ctx), _ctx.Service.GetProfileNames(),
            FocusMode.Groups(_ctx.S).Select(g => g.Name));
        _pick.Items.Clear();
        foreach (var choice in Choices) _pick.Items.Add(LabelOf(choice.Command));
        _pick.SelectedIndex = keep == null ? -1 : Choices.ToList().FindIndex(c => c.Command == keep);
        _line.Text = Line;
        bool any = Choices.Count > 0;
        _noProgram.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _controls.Visibility = ListIsShown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the pick / copy / save / try list shows: only while commands are on and there is a program to write
    /// command lines for. The card and its "Make Stream Deck buttons" button are always there.</summary>
    public bool ListIsShown => _ctx.S.AcceptCommands && Choices.Count > 0;

    /// <summary>"Make Stream Deck buttons": writes the folder of shortcuts and opens it (not while streamer mode is on: the file
    /// names are real character names, so a toast says where the folder is instead). While commands (or, for an elevated
    /// MultiPreview, ordinary programs' commands) are off it first asks one plain question; the safe answer is Cancel, which
    /// changes and writes nothing. Yes turns the switches on through the normal settings path (set, save, follow). The
    /// buttons are labelled with the verb and the real name of each command (<see cref="LabelOf"/>), never a number or a
    /// streamer-masked name, so a file name always means the same command.</summary>
    public void MakeButtons()
    {
        if (_busy) return;   // a click that arrives while the press is still being handled, or queued behind it (see below)
        _busy = true;
        _make.IsEnabled = false;
        try { Make(); }
        finally
        {
            // WPF hands the second click of a double-click to the button only after this handler has returned, so the button
            // stays off until the input already queued has been handled; it comes back on after that.
            _afterQueuedInput(() =>
            {
                _busy = false;
                _make.IsEnabled = true;
            });
        }
    }

    /// <summary>Whether the "Make Stream Deck buttons" button can be pressed: not while a press is being handled, nor until the
    /// input queued behind it has been.</summary>
    public bool MakeButtonEnabled => _make.IsEnabled;

    private void Make()
    {
        if (_exePath() == null)
        {
            _ctx.Host.Toast(SettingsContext.Str("L.S.Cmd.NoProgram"));
            return;
        }
        bool elevated = _elevated();
        bool ask = !_ctx.S.AcceptCommands || (elevated && !_ctx.S.AcceptCommandsUnelevated);
        bool turnedOn = false;
        try
        {
            if (ask)
            {
                if (!_ctx.Host.Confirm(SettingsContext.Str("L.S.Cmd.Consent.Title"),
                        SettingsContext.Str(elevated ? "L.S.Cmd.Consent.BodyElevated" : "L.S.Cmd.Consent.Body"), SafeAnswer.Cancel))
                {
                    _ctx.Host.Toast(SettingsContext.Str("L.S.Cmd.Consent.Kept"));
                    return;
                }
                turnedOn = true;   // before the first switch is touched: whatever throws from here on, the cards are reloaded
                TurnOn("app.general.acceptCommands");
                if (elevated) TurnOn("app.general.acceptCommandsUnelevated");
                _ctx.Applier.Request(ApplyScope.Save);   // saved, and App's Applied path follows the switches (FollowCommandServer)
            }

            var result = _writeFolder(StreamDeckButtons.FolderIn(_ctx.Service.ConfigDirectory), PlanButtons());
            // The shortcut files are named with real character names, so while streamer mode is on Explorer is not opened on them
            // (the stream would show them); the toast, which names no one, says where the buttons are instead.
            bool show = !StreamerMask.IsOn;
            _ctx.Host.Toast(result.Failed != 0
                ? string.Format(SettingsContext.Str("L.S.Cmd.MadePartly"), result.Written, result.Failed)
                : show ? string.Format(SettingsContext.Str("L.S.Cmd.Made"), result.Written) : SettingsContext.Str("L.S.Cmd.MadeNotOpened"));
            if (show) _openFolder(result.Folder);
        }
        finally
        {
            // Last, and even when the write (or a switch) failed: the switches may already be changed and saved, so their cards and
            // this card's list must show their new state. A declined question changed nothing and reloads nothing.
            if (turnedOn) _ctx.Host.Reload();
        }
    }

    /// <summary>Turns one of the command switches on the way its own card does, and lists it in Recently changed (with Undo).</summary>
    private void TurnOn(string settingId)
    {
        var d = SettingsRegistry.Settings.Single(x => x.Id == settingId);
        var before = d.Get(_ctx.S);
        if (SettingVM.Same(before, true)) return;
        d.Set(_ctx.S, true);
        _ctx.Service.Changes.Record(d, SettingChanges.ScopeOf(d, _ctx.S), before, d.Get(_ctx.S));
        d.OnChanged?.Invoke(_ctx);
    }

    /// <summary>The folder's buttons for what runs and is saved now: a focus button per running character on its group's
    /// colour, the groups, the profiles and the fixed commands. Each one's label is <see cref="LabelOf"/> of its command.</summary>
    private IReadOnlyList<ButtonSpec> PlanButtons()
    {
        var s = _ctx.S;
        var characters = NameBar.Entries(CharacterDirectory.Running(_ctx),
            s.ThumbnailGroups.Select(g => (g.Name, (string?)g.Color, (IReadOnlyList<string>)g.Members)).ToList(),
            _ => null, "", Array.Empty<string>());
        var groups = MergedGroups.Build(s).Select(g => (g.Name, g.Colour?.Color));
        return StreamDeckButtons.Plan(characters, groups, _ctx.Service.GetProfileNames(), LabelOf);
    }

    public void Select(int index) => _pick.SelectedIndex = index;

    /// <summary>Puts the picked command's line on the clipboard and says whether Windows allowed it.</summary>
    public void Copy()
    {
        if (Selected is not { } choice) return;
        _ctx.Host.Toast(SettingsContext.Str(_copy(choice.Line) ? "L.S.Cmd.Copied" : "L.S.Cmd.CopyFailed"));
    }

    /// <summary>Asks where, then writes a Windows shortcut that runs the picked command. Cancelled: nothing.</summary>
    public void SaveShortcut()
    {
        if (Selected is not { } choice || _exePath() is not { } exe) return;
        if (CommandLines.Arguments(choice.Command) is not { } arguments) return;
        var path = _ctx.Host.PickSaveFile(ShortcutFile.FileNameFor(LabelOf(choice.Command)), "Shortcut (*.lnk)|*.lnk");
        if (path == null) return;
        _ctx.Host.Toast(SettingsContext.Str(_saveShortcut(path, exe, arguments, null) ? "L.S.Cmd.Saved" : "L.S.Cmd.SaveFailed"));
    }

    /// <summary>Sends the picked command to this MultiPreview as a button would and says what came of it. Awaits the
    /// answer, so the window's thread is free meanwhile; a send that throws is "no answer". This send comes from an
    /// elevated process when MultiPreview is elevated, which the pipe lets in: so while the pipe is closed to ordinary
    /// programs (elevated, second switch off) even "Done" says that a button's program may still get no answer.</summary>
    public async Task TryIt()
    {
        if (Selected is not { } choice) return;
        RemoteOutcome outcome;
        try { outcome = await _send(choice.Command); }
        catch (Exception) { outcome = RemoteOutcome.NotReachable; }
        bool closedToOrdinary = _elevated() && !_ctx.S.AcceptCommandsUnelevated;
        _ctx.Host.Toast(SettingsContext.Str(ResultKey(outcome, closedToOrdinary)));
    }

    /// <summary>The sentence key for an outcome. While the pipe is closed to ordinary programs (MultiPreview elevated and
    /// the second switch off), "Done" and "no answer" each say so: a button's program is not this process.</summary>
    public static string ResultKey(RemoteOutcome outcome, bool closedToOrdinary) => outcome switch
    {
        RemoteOutcome.Done => closedToOrdinary ? "L.S.Cmd.Result.DoneElevated" : "L.S.Cmd.Result.Done",
        RemoteOutcome.Refused => "L.S.Cmd.Result.Refused",
        RemoteOutcome.NotFound => "L.S.Cmd.Result.NotFound",
        _ => closedToOrdinary ? "L.S.Cmd.Result.NotReachableElevated" : "L.S.Cmd.Result.NotReachable",
    };

    /// <summary>The list's text for a command: its label with the name in it, in the window's language.</summary>
    public static string LabelOf(RemoteCommand command) =>
        string.Format(SettingsContext.Str("L.Cmd." + RemoteCommand.WordOf(command.Verb)), command.Argument);
}
