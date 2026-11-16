using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Services;
using EveMultiPreview.Views;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Alert hub › ntfy › Server and topic: the boxes that take the server, the topic and the access token,
/// and the buttons to save them, send a test message, copy the topic, and forget the token or everything. The topic and
/// the token are secrets: they are kept only as NtfySecret's DPAPI text, their boxes are emptied as soon as they are
/// saved and keep no undo, and the card shows only the masked topic. The server is no secret and stays in its box. A
/// topic or token saved on another PC or by another Windows user can't be read here, and the card says to enter it
/// again. The last line says what the server last answered. Shown only while "Send alerts to ntfy" is on.</summary>
public sealed class NtfyEditor : System.Windows.Controls.UserControl
{
    public const string ItemId = EditorIds.NtfyEditor;

    private readonly SettingsContext _ctx;
    private readonly Func<string, string> _protect;
    private readonly Func<string?, string?> _read;
    private readonly TextBox _server = new() { MaxLength = 512, MinWidth = 320, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _topic = new() { IsUndoEnabled = false, MaxLength = 64, MinWidth = 240, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly PasswordBox _token = new() { MaxLength = 256, MinWidth = 240, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _shown = EditorCard.Value("", secondary: true);
    private readonly TextBlock _status = EditorCard.Value("", secondary: true);
    private readonly Button _test, _copy, _forgetToken, _remove;

    /// <param name="protect">Makes the kept text of a topic or token (NtfySecret.Protect; a test hands a fake).</param>
    /// <param name="read">The text behind a kept one, or null (NtfySecret.Unprotect; a test hands a fake).</param>
    public NtfyEditor(SettingsContext ctx, Func<string, string>? protect = null, Func<string?, string?>? read = null)
    {
        _ctx = ctx;
        _protect = protect ?? (plain => NtfySecret.Protect(plain));
        _read = read ?? (stored => NtfySecret.Unprotect(stored));

        System.Windows.Input.KeyEventHandler enter = (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Save(_server.Text, _topic.Text, _token.Password);
        };
        _server.KeyDown += enter;
        _topic.KeyDown += enter;
        _token.KeyDown += enter;
        var random = EditorCard.Button("L.S.Ntfy.Random", () => _topic.Text = NtfyTopic.Random());
        var save = EditorCard.Button("L.S.Ntfy.Save", () => Save(_server.Text, _topic.Text, _token.Password));
        _test = EditorCard.Button("L.S.Ntfy.TestButton", () => _ = Test());
        _copy = EditorCard.Button("L.S.Ntfy.Copy", CopyTopic);
        _forgetToken = EditorCard.Button("L.S.Ntfy.ForgetToken", ForgetToken);
        _remove = EditorCard.Button("L.S.Ntfy.Remove", Remove, destructive: true);

        _shown.TextWrapping = TextWrapping.Wrap;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);   // a screen reader says it when it changes

        var below = new StackPanel();
        below.Children.Add(_shown);
        var server = EditorCard.Field("L.S.Ntfy.ServerBox", _server);
        server.Margin = new Thickness(0, 8, 0, 0);
        below.Children.Add(server);
        below.Children.Add(EditorCard.Row(EditorCard.Field("L.S.Ntfy.TopicBox", _topic), random));
        below.Children.Add(EditorCard.Field("L.S.Ntfy.TokenBox", _token));
        var buttons = EditorCard.WrapRow(save, _test, _copy, _forgetToken, _remove);
        buttons.Margin = new Thickness(0, 4, 0, 0);
        below.Children.Add(buttons);
        below.Children.Add(_status);
        Content = EditorCard.Build(ItemId, null, below);

        ShowSaved();
        // The card comes into view: the page was opened with the switch on, or the switch went on.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ShowStatus(_ctx.Services.NtfyStatus?.Invoke());
        };
    }

    /// <summary>What the card shows of the saved server, topic and token.</summary>
    public string Shown => _shown.Text;

    /// <summary>The card's last line: what the server last answered, or what is wrong with what was entered.</summary>
    public string StatusLine => _status.Text;

    /// <summary>Keeps what was entered. The server must be an http(s) address (empty: ntfy.sh); an empty topic box keeps
    /// the saved topic, and an empty token box the saved token. False, with the reason on the last line and the boxes
    /// as they were, for a server, topic or token that is refused. Otherwise the topic and token are kept as DPAPI text,
    /// their boxes are emptied and the masked topic is shown. Nothing entered is put anywhere else.</summary>
    public bool Save(string server, string topic, string token)
    {
        if (!NtfyServer.TryNormalize(server, out var kept))
        {
            Say(Str("L.S.Ntfy.NotAServer"));
            return false;
        }
        topic = (topic ?? "").Trim();
        token = (token ?? "").Trim();
        if (topic.Length > 0 && !NtfyTopic.IsValid(topic))
        {
            Say(Str("L.S.Ntfy.NotATopic"));
            return false;
        }
        if (topic.Length == 0 && string.IsNullOrEmpty(_ctx.S.NtfyTopic))
        {
            Say(Str("L.S.Ntfy.NeedTopic"));
            return false;
        }
        if (token.Length > 0 && !NtfyTopic.IsValidToken(token))
        {
            Say(Str("L.S.Ntfy.NotAToken"));
            return false;
        }
        string? keptTopic = null, keptToken = null;
        try
        {
            if (topic.Length > 0) keptTopic = _protect(topic);
            if (token.Length > 0) keptToken = _protect(token);
        }
        catch (Exception)
        {
            // DPAPI refused (no profile loaded): nothing is kept, and nothing entered is shown or logged.
            Say(Str("L.S.Ntfy.Unreadable"));
            return false;
        }
        _ctx.S.NtfyServer = kept;
        if (keptTopic != null) _ctx.S.NtfyTopic = keptTopic;
        if (keptToken != null) _ctx.S.NtfyToken = keptToken;
        _ctx.Applier.Request(ApplyScope.Save);
        _server.Text = kept;
        _topic.Clear();
        _token.Clear();
        Say("");
        ShowSaved();
        return true;
    }

    /// <summary>Forgets the topic and the token, after the player said yes. The server stays.</summary>
    public void Remove()
    {
        if (!_ctx.Host.Confirm(Str("L.S.Ntfy.RemoveTitle"), Str("L.S.Ntfy.RemoveBody"))) return;
        _ctx.S.NtfyTopic = "";
        _ctx.S.NtfyToken = "";
        _ctx.Applier.Request(ApplyScope.Save);
        Say("");
        ShowSaved();
    }

    /// <summary>Forgets the token only (a server that no longer asks for one).</summary>
    public void ForgetToken()
    {
        _ctx.S.NtfyToken = "";
        _ctx.Applier.Request(ApplyScope.Save);
        Say("");
        ShowSaved();
    }

    /// <summary>The button: the app sends one test message, and the line says what the server answered. A window opened
    /// without the app has nobody to send it: then nothing changes.</summary>
    public async Task Test()
    {
        var test = _ctx.Services.TestNtfy;
        if (test == null) return;
        PushResult result;
        try { result = await test(); }
        catch (Exception) { result = new PushResult(PushOutcome.Unreachable); }
        ShowStatus(new PushStatus(result.Outcome, result.Status, DateTime.UtcNow));
    }

    /// <summary>Puts the saved topic on the clipboard, to subscribe to it in the ntfy app. Shown nowhere.</summary>
    private void CopyTopic()
    {
        var topic = ReadSafe(_ctx.S.NtfyTopic);
        if (topic == null) return;
        try
        {
            System.Windows.Clipboard.SetText(topic);
            Say(Str("L.S.Ntfy.Copied"));
        }
        catch (Exception) { Say(Str("L.S.Ntfy.CopyFailed")); }   // another program holds the clipboard
    }

    private string? ReadSafe(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return _read(stored); }
        catch (Exception) { return null; }
    }

    private void ShowSaved()
    {
        var s = _ctx.S;
        _server.Text = s.NtfyServer ?? "";
        string shown;
        var topic = ReadSafe(s.NtfyTopic);
        if (string.IsNullOrEmpty(s.NtfyTopic)) shown = Str("L.S.Ntfy.NoTopic");
        else if (topic == null) shown = Str("L.S.Ntfy.Unreadable");
        else
        {
            shown = string.Format(CultureInfo.CurrentCulture, Str("L.S.Ntfy.Saved"), NtfyServer.Shown(s.NtfyServer), NtfyTopic.Masked(topic));
            if (!string.IsNullOrEmpty(s.NtfyToken))
                shown += " " + (ReadSafe(s.NtfyToken) == null ? Str("L.S.Ntfy.TokenUnreadable") : Str("L.S.Ntfy.TokenSaved"));
        }
        _shown.Text = shown;
        _test.Visibility = topic != null ? Visibility.Visible : Visibility.Collapsed;
        _copy.Visibility = topic != null ? Visibility.Visible : Visibility.Collapsed;
        _forgetToken.Visibility = string.IsNullOrEmpty(s.NtfyToken) ? Visibility.Collapsed : Visibility.Visible;
        _remove.Visibility = string.IsNullOrEmpty(s.NtfyTopic) && string.IsNullOrEmpty(s.NtfyToken) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowStatus(PushStatus? status)
    {
        if (status == null) return;
        Say(status.Outcome switch
        {
            PushOutcome.Sent => string.Format(CultureInfo.CurrentCulture, Str("L.S.Ntfy.Status.Sent"), status.AtUtc.ToLocalTime()),
            PushOutcome.Gone => string.Format(CultureInfo.CurrentCulture, Str("L.S.Ntfy.Status.Gone"), status.Status),
            PushOutcome.Refused => string.Format(CultureInfo.CurrentCulture, Str("L.S.Ntfy.Status.Refused"), status.Status),
            PushOutcome.RateLimited => Str("L.S.Ntfy.Status.Limited"),
            _ => Str("L.S.Ntfy.Status.Unreachable"),
        });
    }

    private void Say(string text)
    {
        bool changed = text != _status.Text;
        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (changed && text.Length > 0) Announce.Changed(_status);     // a live region announces nothing by itself
    }

    private static string Str(string key) => SettingsContext.Str(key);
}
