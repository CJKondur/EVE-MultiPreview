using System;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #2: what is taken as a Discord webhook link, and where it may be sent. The link is a secret (it is
/// the whole credential), so nothing here puts it in an exception, a message or a log: a refusal is a plain
/// <c>false</c>, never an echo of the text.</summary>
public static class DiscordWebhook
{
    /// <summary>The one host a webhook is ever sent to.</summary>
    public const string Host = "discord.com";

    private const int MaxLength = 512;

    // Pasted links may come from the other hosts Discord has used; all are rewritten to Host.
    private static readonly string[] PasteHosts = { "discord.com", "ptb.discord.com", "canary.discord.com", "discordapp.com" };

    // As pasted: /api, optionally /api/v<n>, then /webhooks/<id>/<token>, optionally a closing slash.
    private static readonly Regex PastedPath = new(@"^/api(/v\d{1,2})?/webhooks/(\d{17,20})/([A-Za-z0-9_-]{30,100})/?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // As sent: exactly the rewritten shape.
    private static readonly Regex SendPath = new(@"^/api/webhooks/(\d{17,20})/([A-Za-z0-9_-]{30,100})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when <paramref name="text"/> is a Discord webhook link; <paramref name="webhook"/> is then the
    /// link rewritten to <c>https://discord.com/api/webhooks/&lt;id&gt;/&lt;token&gt;</c>. Takes https only, no port,
    /// user info, query or fragment, no look-alike or IDN host.</summary>
    public static bool TryParse(string? text, out Uri webhook)
    {
        webhook = null!;
        if (text == null) return false;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxLength) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;

        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length != 0) return false;
        if (!uri.IsDefaultPort) return false;
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        if (!string.Equals(uri.IdnHost, uri.Host, StringComparison.Ordinal)) return false;   // IDN / punycode look-alikes
        if (Array.IndexOf(PasteHosts, uri.Host) < 0) return false;                           // Uri.Host is already lower case

        var m = PastedPath.Match(uri.AbsolutePath);
        if (!m.Success) return false;

        webhook = new Uri("https://" + Host + "/api/webhooks/" + m.Groups[2].Value + "/" + m.Groups[3].Value);
        return true;
    }

    /// <summary>True only for exactly the shape <see cref="TryParse"/> writes. The sender asks this before every request.</summary>
    public static bool IsSendable(Uri? webhook)
    {
        if (webhook == null || !webhook.IsAbsoluteUri) return false;
        return webhook.Scheme == Uri.UriSchemeHttps
            && webhook.Host == Host
            && webhook.IsDefaultPort
            && webhook.UserInfo.Length == 0
            && webhook.Query.Length == 0
            && webhook.Fragment.Length == 0
            && SendPath.IsMatch(webhook.AbsolutePath);
    }

    /// <summary>What may be shown of a saved link: the host and path without the token, and the last four digits of
    /// the webhook's id.</summary>
    public static string Masked(Uri webhook)
    {
        var shown = Host + "/api/webhooks/…";
        if (webhook == null || !webhook.IsAbsoluteUri) return shown;
        var m = SendPath.Match(webhook.AbsolutePath);
        if (!m.Success) return shown;
        var id = m.Groups[1].Value;
        return shown + id.Substring(id.Length - 4);
    }
}
