using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>App settings › Diagnostics › Weekly playtime (IDEAS P11).</summary>
public static class PlaytimeActions
{
    /// <summary>Clear playtime history. The button asks first (ActionDef.Confirm, default Cancel); this deletes the file and the
    /// ones set aside and empties what the running app holds. Without a running manager (the settings opened alone) the file in
    /// the settings folder goes.</summary>
    public static void Clear(SettingsContext ctx)
    {
        var tm = ctx.Services.Thumbnails;
        if (tm != null) tm.ClearPlaytime();
        else new PlaytimeStore(ctx.Service.ConfigDirectory).Delete();
        ctx.Host.Toast(SettingsContext.Str("L.S.Playtime.Cleared"));
    }
}
