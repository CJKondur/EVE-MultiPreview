using System;
using System.Diagnostics;
using EveMultiPreview.Services;
using EveMultiPreview.Views;

namespace EveMultiPreview.SettingsUi;

/// <summary>About &amp; updates actions, ported from SettingsWindow.Panels2.cs:2029-2095.</summary>
public static class AboutActions
{
    public const string RepoUrl = "https://github.com/CJKondur/EVE-MultiPreview";

    public static string VersionText => typeof(AboutActions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void OpenGitHub()
    {
        try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Asks GitHub for a newer release (the only network call, and only on this click);
    /// offers the update dialog when there is one.</summary>
    public static async void CheckNow(SettingsContext ctx)
    {
        ctx.Host.Toast(SettingsContext.Str("L.S.About.Checking"));
        try
        {
            var service = new UpdateService();
            if (await service.CheckForUpdateAsync(ctx.S.ReceivePreReleaseUpdates))
                new UpdateDialog(service) { Owner = ctx.Host.DialogOwner }.ShowDialog();
            else
                ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.About.UpToDate"), VersionText));
        }
        catch (Exception ex)
        {
            ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.About.CheckFailed"), ex.Message));
        }
    }
}
