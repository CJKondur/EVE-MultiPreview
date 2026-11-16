using System;
using System.Security.Cryptography;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>ntfy's topic and access token as kept in the settings file: encrypted with DPAPI for the current Windows
/// user, as <c>dpapi1:</c> + base64, the same form as WebhookSecret's (so the bug report's scrub of <c>dpapi1:</c> text
/// covers them too), with entropy of their own. A settings file copied to another PC or user holds nothing usable:
/// <see cref="Unprotect"/> answers null and the card says to enter them again. Neither is ever logged or put in an
/// exception or a message here; every failure is a null, not a text.</summary>
public static class NtfySecret
{
    public const string Prefix = "dpapi1:";

    /// <summary>Extra entropy so another program's DPAPI blob for the same user is not read as ours.</summary>
    private const string Entropy = "EVE MultiPreview ntfy v1";

    private static byte[] EntropyBytes => Encoding.UTF8.GetBytes(Entropy);

    /// <summary>The kept form of <paramref name="plain"/>. <paramref name="protect"/> is for tests; the default is DPAPI
    /// for the current user. Throws <see cref="ArgumentException"/> (fixed text) for an empty text.</summary>
    public static string Protect(string plain, Func<byte[], byte[]>? protect = null)
    {
        if (string.IsNullOrEmpty(plain)) throw new ArgumentException("Nothing to keep.");
        protect ??= data => ProtectedData.Protect(data, EntropyBytes, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protect(Encoding.UTF8.GetBytes(plain)));
    }

    /// <summary>The text behind a kept form, or null for: nothing, no prefix, bad base64, or a transform that fails
    /// (another user or PC). Never throws.</summary>
    public static string? Unprotect(string? stored, Func<byte[], byte[]>? unprotect = null)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            unprotect ??= data => ProtectedData.Unprotect(data, EntropyBytes, DataProtectionScope.CurrentUser);
            var plain = Encoding.UTF8.GetString(unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length))));
            return plain.Length > 0 ? plain : null;
        }
        catch (Exception)
        {
            // FormatException, CryptographicException, or a platform that has no DPAPI: all "nothing".
            return null;
        }
    }
}
