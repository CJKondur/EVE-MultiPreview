using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

internal sealed partial class SettingsEditors
{
    /// <summary>ntfy: the boxes that take the server, the topic and the access token, and the buttons to save them, send a
    /// test message, copy the topic, and forget the token or everything, as the Windows NtfyEditor does. The topic and the
    /// token are secrets: they are kept in files only the owner can read (NtfyFile), never in the settings file (which
    /// holds a marker), their boxes are emptied once saved, and only the masked topic is shown again. A topic or token the
    /// Windows app encrypted for its Windows user (dpapi1:) can't be read here, and the page says to enter it again.</summary>
    internal Control Ntfy()
    {
        var card = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4) };
        var shown = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, IsVisible = false };
        var server = new TextBox { Width = 420, MaxLength = 512, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = T("L.S.Ntfy.ServerBox", "Server (empty: ntfy.sh)") };
        var topic = new TextBox { Width = 300, MaxLength = 64, IsUndoEnabled = false, PlaceholderText = T("L.S.Ntfy.TopicBox", "Topic") };
        var token = new TextBox { Width = 300, MaxLength = 256, IsUndoEnabled = false, PasswordChar = '•', HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = T("L.S.Ntfy.TokenBox", "Access token (optional)") };
        var random = new Button { Content = Ui.NoAccess(T("L.S.Ntfy.Random", "Make a rando_m topic")) };
        var save = new Button { Content = Ui.NoAccess(T("L.S.Ntfy.Save", "Sa_ve")) };
        var test = new Button { Content = Ui.NoAccess(T("L.S.Ntfy.TestButton", "Send a test _notification")) };
        var copy = new Button { Content = Ui.NoAccess(T("L.S.Ntfy.Copy", "_Copy topic")) };
        var forgetToken = new Button { Content = Ui.NoAccess(T("L.S.Ntfy.ForgetToken", "Forget t_oken")) };
        var remove = new Button { Content = Ui.NoAccess(T("L.S.Ntfy.Remove", "Remove topic and token")) };

        void Say(string text) { status.Text = text; status.IsVisible = text.Length > 0; }
        string? Read(string file, string? stored) => NtfyFile.Read(file, stored);
        void ShowSaved()
        {
            server.Text = S.NtfyServer ?? "";
            var storedTopic = S.NtfyTopic ?? "";
            var storedToken = S.NtfyToken ?? "";
            string? plain = null;
            if (storedTopic.Length == 0) shown.Text = T("L.S.Ntfy.NoTopic", "No topic yet. Make a random one (anybody who knows a topic on ntfy.sh can read it), save it, then subscribe to it in the ntfy app.");
            else if (NtfyFile.IsWindowsEncrypted(storedTopic))
                shown.Text = T("L.Linux.NtfyDpapi", "The saved topic comes from the Windows app, where it is encrypted for that Windows user. It can't be read here: enter it again.");
            else if ((plain = Read(NtfyFile.TopicFile, storedTopic)) == null)
                shown.Text = T("L.S.Ntfy.Unreadable", "The saved topic or token can't be read here: it was saved on another PC or by another user. Enter it again.");
            else
            {
                var line = string.Format(T("L.S.Ntfy.Saved", "Server: {0} · Topic: {1}"), NtfyServer.Shown(S.NtfyServer), NtfyTopic.Masked(plain));
                if (storedToken.Length > 0)
                    line += " " + (Read(NtfyFile.TokenFile, storedToken) == null
                        ? T("L.S.Ntfy.TokenUnreadable", "· The saved token can't be read here: enter it again.")
                        : T("L.S.Ntfy.TokenSaved", "· Token saved"));
                shown.Text = line + "\n" + string.Format(T("L.Linux.NtfyKeptIn", "The topic and token are kept in files only you can read, in {0}"), AppPaths.ConfigDir);
            }
            test.IsVisible = plain != null;
            copy.IsVisible = plain != null;
            forgetToken.IsVisible = storedToken.Length > 0;
            remove.IsVisible = storedTopic.Length > 0 || storedToken.Length > 0;
        }
        void Show(PushStatus? last)
        {
            if (last == null) return;
            var c = System.Globalization.CultureInfo.CurrentCulture;
            Say(last.Outcome switch
            {
                PushOutcome.Sent => string.Format(c, T("L.S.Ntfy.Status.Sent", "The last message reached ntfy at {0:t}."), last.AtUtc.ToLocalTime()),
                PushOutcome.Gone => string.Format(c, T("L.S.Ntfy.Status.Gone", "The server refused the topic or the token ({0}). Nothing is sent until you change them."), last.Status),
                PushOutcome.Refused => string.Format(c, T("L.S.Ntfy.Status.Refused", "The server refused the last message ({0})."), last.Status),
                PushOutcome.RateLimited => T("L.S.Ntfy.Status.Limited", "The server asked MultiPreview to slow down. Messages wait their turn."),
                _ => T("L.S.Ntfy.Status.Unreachable", "The ntfy server could not be reached. MultiPreview keeps trying for 5 minutes per alert."),
            });
        }
        void SaveAll()
        {
            if (!NtfyServer.TryNormalize(server.Text, out var kept))
            {
                Say(T("L.S.Ntfy.NotAServer", "That is not a server address. It looks like https://ntfy.example.com (or leave it empty for ntfy.sh)."));
                return;
            }
            var t = (topic.Text ?? "").Trim();
            var k = (token.Text ?? "").Trim();
            if (t.Length > 0 && !NtfyTopic.IsValid(t))
            {
                Say(T("L.S.Ntfy.NotATopic", "A topic is 1 to 64 letters, digits, - and _."));
                return;
            }
            if (t.Length == 0 && string.IsNullOrEmpty(S.NtfyTopic))
            {
                Say(T("L.S.Ntfy.NeedTopic", "Enter a topic first, or make a random one."));
                return;
            }
            if (k.Length > 0 && !NtfyTopic.IsValidToken(k))
            {
                Say(T("L.S.Ntfy.NotAToken", "That is not an access token: it has spaces or characters a token never has."));
                return;
            }
            string? keptTopic = null, keptToken = null;
            try
            {
                if (t.Length > 0) keptTopic = NtfyFile.Save(NtfyFile.TopicFile, t);
                if (k.Length > 0) keptToken = NtfyFile.Save(NtfyFile.TokenFile, k);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say(T("L.Linux.NtfyWriteFailed", "The topic or token could not be saved: its file could not be written."));
                return;
            }
            S.NtfyServer = kept;
            if (keptTopic != null) S.NtfyTopic = keptTopic;
            if (keptToken != null) S.NtfyToken = keptToken;
            Save();
            _host.AlertsChanged();
            topic.Text = "";
            token.Text = "";
            Say("");
            ShowSaved();
        }
        server.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; SaveAll(); } };
        topic.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; SaveAll(); } };
        token.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; SaveAll(); } };
        random.Click += (_, _) => topic.Text = NtfyTopic.Random();
        save.Click += (_, _) => SaveAll();
        copy.Click += async (_, _) =>
        {
            var plain = Read(NtfyFile.TopicFile, S.NtfyTopic);
            if (plain == null || TopLevel.GetTopLevel(copy)?.Clipboard is not { } clipboard) return;
            try
            {
                await clipboard.SetTextAsync(plain);
                Say(T("L.S.Ntfy.Copied", "The topic is on the clipboard."));
            }
            catch (Exception) { Say(T("L.S.Ntfy.CopyFailed", "The topic could not be put on the clipboard.")); }
        };
        forgetToken.Click += (_, _) =>
        {
            S.NtfyToken = "";
            NtfyFile.Delete(NtfyFile.TokenFile);
            Save();
            _host.AlertsChanged();
            Say("");
            ShowSaved();
        };
        remove.Click += (_, _) =>
        {
            S.NtfyTopic = "";
            S.NtfyToken = "";
            NtfyFile.Delete(NtfyFile.TopicFile);
            NtfyFile.Delete(NtfyFile.TokenFile);
            Save();
            _host.AlertsChanged();
            Say("");
            ShowSaved();
        };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            PushResult result;
            try { result = await _host.TestNtfy(); }
            catch (Exception) { result = new PushResult(PushOutcome.Unreachable); }
            test.IsEnabled = true;
            Show(new PushStatus(result.Outcome, result.Status, DateTime.UtcNow));
        };

        var topicRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topicRow.Children.Add(topic);
        topicRow.Children.Add(random);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { save, test, copy, forgetToken, remove })
        {
            b.Margin = new Thickness(0, 0, 8, 4);
            buttons.Children.Add(b);
        }
        card.Children.Add(shown);
        card.Children.Add(server);
        card.Children.Add(topicRow);
        card.Children.Add(token);
        card.Children.Add(buttons);
        card.Children.Add(status);
        ShowSaved();
        Show(_host.NtfyStatus);
        return Ui.Card(card);
    }
}
