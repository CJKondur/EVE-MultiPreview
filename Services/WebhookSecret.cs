using System;
using System.Security.Cryptography;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #2: the Discord webhook link as kept in the settings file: encrypted with DPAPI for the current
/// Windows user, as <c>dpapi1:</c> + base64. A settings file copied to another PC or user (an export, a backup) then
/// holds nothing usable: <see cref="Unprotect"/> answers null and the card says "paste it again". The link is never
/// logged and never put in an exception or a message here; every failure is a null, not a text.</summary>
public static class WebhookSecret
{
    public const string Prefix = "dpapi1:";

    /// <summary>Extra entropy so another program's DPAPI blob for the same user is not read as ours.</summary>
    private const string Entropy = "EVE MultiPreview Discord webhook v1";

    private static byte[] EntropyBytes => Encoding.UTF8.GetBytes(Entropy);

    /// <summary>The kept form of <paramref name="webhook"/>. <paramref name="protect"/> is for tests; the default is DPAPI for the
    /// current user. Throws <see cref="ArgumentException"/> (fixed text) for a link that is not sendable.</summary>
    public static string Protect(Uri webhook, Func<byte[], byte[]>? protect = null)
    {
        if (!DiscordWebhook.IsSendable(webhook)) throw new ArgumentException("Not a sendable Discord webhook link.");
        protect ??= data => ProtectedData.Protect(data, EntropyBytes, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protect(Encoding.UTF8.GetBytes(webhook.AbsoluteUri)));
    }

    /// <summary>The link behind a kept form, or null for: nothing, no prefix, bad base64, a transform that fails (another
    /// user or PC), or a text that is not a sendable Discord link. Never throws.</summary>
    public static Uri? Unprotect(string? stored, Func<byte[], byte[]>? unprotect = null)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            unprotect ??= data => ProtectedData.Unprotect(data, EntropyBytes, DataProtectionScope.CurrentUser);
            var plain = Encoding.UTF8.GetString(unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length))));
            return Uri.TryCreate(plain, UriKind.Absolute, out var uri) && DiscordWebhook.IsSendable(uri) ? uri : null;
        }
        catch (Exception)
        {
            // FormatException, CryptographicException, ArgumentException, or a platform that has no DPAPI: all "no link".
            return null;
        }
    }
}
