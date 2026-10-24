using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › General › Other programs › Commands for buttons (IDEAS #15): a list of every command a button can
/// run (the fixed ones, one per running character, group and profile), its command line, and three buttons: copy the line,
/// save it as a Windows shortcut, and try it (send it to this MultiPreview the way a button would, and say what came of it).
/// Writes no setting. A player's name is only ever an argument of string.Format here, never its format.</summary>
public sealed class CommandButtonsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "app.general.buttons";
    private readonly SettingsContext _ctx;
    private readonly Func<string?> _exePath;
    private readonly Func<string, bool> _copy;
    private readonly Func<string, string, string, bool> _saveShortcut;
    private readonly Func<RemoteCommand, Task<RemoteOutcome>> _send;
    private readonly Func<bool> _elevated;
    private readonly ComboBox _pick = new() { MinWidth = 360 };
    private readonly TextBox _line = new() { IsReadOnly = true, MinWidth = 360 };
    private readonly UIElement _noProgram = EditorCard.Note("L.S.Cmd.NoProgram");
    private readonly StackPanel _controls = new();

    /// <summary>The real seams: this program's path, the clipboard, the shortcut writer and the pipe (asynchronously: a
    /// window's own thread never waits for the answer).</summary>
    public CommandButtonsEditor(SettingsContext ctx) : this(ctx, () => CommandLines.ButtonProgram(Environment.ProcessPath), ClipboardText.Set, ShortcutFile.Create,
        command => CommandPipe.SendAsync(CommandPipe.CurrentName(), command, CommandPipe.SendTime))
    {
    }

    /// <param name="elevated">Whether this MultiPreview runs as administrator; the real answer unless a test hands one in.</param>
    public CommandButtonsEditor(SettingsContext ctx, Func<string?> exePath, Func<string, bool> copy,
        Func<string, string, string, bool> saveShortcut, Func<RemoteCommand, Task<RemoteOutcome>> send, Func<bool>? elevated = null)
    {
        _ctx = ctx;
        _exePath = exePath;
        _copy = copy;
        _saveShortcut = saveShortcut;
        _send = send;
        _elevated = elevated ?? CommandPipe.IsElevated;
        _pick.SelectionChanged += (_, _) => _line.Text = Line;
        EditorCard.NameFrom(_line, "L.S." + ItemId);
        _controls.Children.Add(EditorCard.Field("L.S.Cmd.Pick", _pick));
        _controls.Children.Add(_line);
        var buttons = EditorCard.Row(EditorCard.Button("L.S.Cmd.Copy", Copy), EditorCard.Button("L.S.Cmd.Shortcut", SaveShortcut),
            EditorCard.Button("L.S.Cmd.TryIt", () => _ = TryIt()));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        _controls.Children.Add(buttons);
        var below = new StackPanel();
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
        _controls.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
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
        _ctx.Host.Toast(SettingsContext.Str(_saveShortcut(path, exe, arguments) ? "L.S.Cmd.Saved" : "L.S.Cmd.SaveFailed"));
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
