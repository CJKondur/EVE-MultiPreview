using System.Linq;
using System.Windows.Controls;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Clients › Windows &amp; positions › Never minimize (Profile.DontMinimizeClients): chips,
/// Add… and Add all running clients. A name is listed once, whatever its case.</summary>
public sealed class NeverMinimizeEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;

    public NeverMinimizeEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Refresh();
    }

    public void Refresh()
    {
        var list = CharacterDirectory.Current(_ctx.S).DontMinimizeClients;
        var chips = new WrapPanel();
        foreach (var n in list.ToList())
        {
            var name = n;
            chips.Children.Add(EditorCard.Chip(name, () => { CharacterFields.SetNeverMinimize(_ctx.S, name, false); Changed(); }));
        }
        if (list.Count == 0) chips.Children.Add(EditorCard.Note("L.S.clients.windows.neverMinimize.Empty"));
        Content = EditorCard.Build("clients.windows.neverMinimize", EditorCard.Row(
            EditorCard.Button("L.S.NeverMinimize.Add", Add),
            EditorCard.Button("L.S.Running.AddAll", AddRunning)), chips);
    }

    /// <summary>Old OnDontMinAdd (now without duplicates). Public for tests.</summary>
    public void Add()
    {
        var name = _ctx.Host.PickCharacter(SettingsContext.Str("L.S.NeverMinimize.AddTitle"),
            CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
        if (name == null) return;
        CharacterFields.SetNeverMinimize(_ctx.S, name, true);
        Changed();
    }

    private void AddRunning()
    {
        var running = CharacterDirectory.Running(_ctx);
        if (running.Count == 0) { _ctx.Host.Toast(SettingsContext.Str("L.S.Running.None")); return; }
        var added = running.Count(n => !CharacterFields.NeverMinimize(_ctx.S, n));
        if (added == 0) { _ctx.Host.Toast(SettingsContext.Str("L.S.Running.AllThere")); return; }
        foreach (var n in running) if (!CharacterFields.NeverMinimize(_ctx.S, n)) CharacterFields.SetNeverMinimize(_ctx.S, n, true);
        _ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.Running.Added"), added));
        Changed();
    }

    private void Changed()
    {
        _ctx.Applier.Request(ApplyScope.Save);   // read on every switch by MinimizeInactiveClients
        Refresh();
    }
}
