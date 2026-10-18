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

/// <summary>Alerts › Alert hub › Phone and Discord › Discord webhook (IDEAS #2): the box that takes the webhook link,
/// and the buttons to save it, send a test message and remove it. The link is a secret: it is kept only as
/// WebhookSecret's DPAPI text, the box is emptied as soon as it is saved and keeps no undo, and the card shows only
/// the masked link (DiscordWebhook.Masked). A link saved on another PC or by another Windows user can't be read here,
/// and the card says to paste it again. The last line says what Discord last answered, read from the app when the
/// card comes into view and after a test. Shown only while "Send alerts to Discord" is on.</summary>
public sealed class PushWebhookEditor : System.Windows.Controls.UserControl
{
    public const string ItemId = "alerts.hub.pushWebhook";

    private readonly SettingsContext _ctx;
    private readonly Func<Uri, string> _protect;
    private readonly Func<string?, Uri?> _read;
    private readonly TextBox _box = new() { IsUndoEnabled = false, MaxLength = 512, MinWidth = 320, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _shown = EditorCard.Value("", secondary: true);
    private readonly TextBlock _status = EditorCard.Value("", secondary: true);
    private readonly Button _test, _remove;

    /// <param name="protect">Makes the kept text of a link (WebhookSecret.Protect; a test hands a fake).</param>
    /// <param name="read">The link behind the kept text, or null (WebhookSecret.Unprotect; a test hands a fake).</param>
    public PushWebhookEditor(SettingsContext ctx, Func<Uri, string>? protect = null, Func<string?, Uri?>? read = null)
    {
        _ctx = ctx;
        _protect = protect ?? (link => WebhookSecret.Protect(link));
        _read = read ?? (stored => WebhookSecret.Unprotect(stored));

        Loc.SetNameKey(_box, "L.S.Push.LinkBox");
        _box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Save(_box.Text);
        };
        var save = EditorCard.Button("L.S.Push.Save", () => Save(_box.Text));
        _test = EditorCard.Button("L.S.Push.TestButton", () => _ = Test());
        _remove = EditorCard.Button("L.S.Push.Remove", Remove, destructive: true);

        _shown.TextWrapping = TextWrapping.Wrap;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);   // a screen reader says it when it changes

        var below = new StackPanel();
        below.Children.Add(_shown);
        // The box has a label and 320 px of its own: the buttons go on the row below, or in a wide-fonted look
        // (terminal, ink) the save button leaves the card at 960 px.
        var entry = EditorCard.Field("L.S.Push.LinkBox", _box);
        entry.Margin = new Thickness(0, 8, 0, 0);
        below.Children.Add(entry);
        var buttons = EditorCard.Row(save, _test, _remove);
        buttons.Margin = new Thickness(0, 4, 0, 0);
        below.Children.Add(buttons);
        below.Children.Add(_status);
        Content = EditorCard.Build(ItemId, null, below);

        ShowLink();
        // The card comes into view: the page was opened with the switch on, or the switch went on.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ShowStatus(_ctx.Services.PushStatus?.Invoke());
        };
    }

    /// <summary>What the card shows of the saved link: the masked link, or that there is none, or that this PC
    /// can't read it.</summary>
    public string Shown => _shown.Text;

    /// <summary>The card's last line: what Discord last answered, or that a pasted text is no webhook link.</summary>
    public string StatusLine => _status.Text;

    /// <summary>What the box holds (empty after a save).</summary>
    public string BoxText
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    /// <summary>Keeps a pasted link: false for a text DiscordWebhook.TryParse refuses (the line says so, the box keeps
    /// it so it can be put right). Otherwise the kept text is stored and saved, the box is emptied and the masked link
    /// shown. Nothing of the pasted text is put anywhere else.</summary>
    public bool Save(string pasted)
    {
        if (!DiscordWebhook.TryParse(pasted, out var link))
        {
            Say(Str("L.S.Push.NotAWebhook"));
            return false;
        }
        string kept;
        try { kept = _protect(link); }
        catch (Exception)
        {
            // DPAPI refused (no profile loaded): nothing is kept, and the link is not shown or logged.
            Say(Str("L.S.Push.Unreadable"));
            return false;
        }
        _ctx.S.PushDiscordWebhook = kept;
        _ctx.Applier.Request(ApplyScope.Save);
        _box.Clear();
        Say("");
        ShowLink();
        return true;
    }

    /// <summary>Forgets the link, after the player said yes. The webhook stays in Discord until it is deleted there.</summary>
    public void Remove()
    {
        if (!_ctx.Host.Confirm(Str("L.S.Push.RemoveTitle"), Str("L.S.Push.RemoveBody"))) return;
        _ctx.S.PushDiscordWebhook = "";
        _ctx.Applier.Request(ApplyScope.Save);
        Say("");
        ShowLink();
    }

    /// <summary>The button: the app sends one test message, and the line says what Discord answered. A window opened
    /// without the app has nobody to send it: then nothing changes.</summary>
    public async Task Test()
    {
        var test = _ctx.Services.TestPush;
        if (test == null) return;
        PushResult result;
        try { result = await test(); }
        catch (Exception) { result = new PushResult(PushOutcome.Unreachable); }
        ShowStatus(new PushStatus(result.Outcome, result.Status, DateTime.UtcNow));
    }

    private void ShowLink()
    {
        var stored = _ctx.S.PushDiscordWebhook;
        string shown;
        if (string.IsNullOrEmpty(stored)) shown = Str("L.S.Push.NoLink");
        else
        {
            Uri? link;
            try { link = _read(stored); }
            catch (Exception) { link = null; }
            shown = link == null ? Str("L.S.Push.Unreadable") : DiscordWebhook.Masked(link);
        }
        _shown.Text = shown;
        var saved = string.IsNullOrEmpty(stored) ? Visibility.Collapsed : Visibility.Visible;
        _test.Visibility = saved;
        _remove.Visibility = saved;
    }

    private void ShowStatus(PushStatus? status)
    {
        if (status == null) return;
        Say(status.Outcome switch
        {
            PushOutcome.Sent => string.Format(CultureInfo.CurrentCulture, Str("L.S.Push.Status.Sent"), status.AtUtc.ToLocalTime()),
            PushOutcome.Gone => Str("L.S.Push.Status.Gone"),
            PushOutcome.Refused => string.Format(CultureInfo.CurrentCulture, Str("L.S.Push.Status.Refused"), status.Status),
            PushOutcome.RateLimited => Str("L.S.Push.Status.Limited"),
            _ => Str("L.S.Push.Status.Unreachable"),
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
