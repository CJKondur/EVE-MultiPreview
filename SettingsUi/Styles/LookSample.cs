using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>What a look's miniature shows (UI styles spec §9.2, Fx.Preview): two navigation entries, the open one first,
/// a section heading, a setting with its on/off control, a slider, a dropdown, a number box and a button. They are the
/// app's own view models of three real settings of Alerts › Sounds, over settings of their own that nothing reads and
/// nothing saves: the miniature is inert, and if it were not, it would change nothing.</summary>
public sealed class LookSample
{
    public LookSample()
    {
        // A file path only because SettingsService wants one: nothing loads or saves it.
        var service = new SettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EVE MultiPreview look sample.json"));
        // Off (owner decision S2-15). On, Windows 11's switch has a ControlTemplate trigger that is true when the template
        // is made and sets DynamicResources; inside a scoped miniature WPF then throws a NullReferenceException in
        // StyleHelper.GetInstanceValue during a layout pass, where nothing can catch it (measured in plans S2 and S3).
        service.Settings.EnableAlertSounds = false;
        service.Settings.AlertSoundVolume = 70;
        var ctx = new SettingsContext(service, new SettingsApplier(() => { }, () => { }, () => { }, () => { }, new Never()),
            new AppServices(), new Inert());
        SettingVM Card(string id) => new(SettingsRegistry.Settings.First(d => d.Id == id), ctx);
        Toggle = Card("alerts.sounds.enabled");
        Slider = Card("alerts.sounds.volume");
        Choice = Card("alerts.sounds.speakFrom");
        Number = Card("alerts.sounds.escalateAfter");
        Entries = new[] { AlertsPages.SoundsId, AlertsPages.EventsId }
            .Select(id => new NavPageVM(SettingsRegistry.Page(id)!, _ => { }) { IsSelected = id == AlertsPages.SoundsId }).ToList();
    }

    public IReadOnlyList<NavPageVM> Entries { get; }

    /// <summary>The heading over the cards: Alerts › Sounds' first section.</summary>
    public string SectionTitleKey => "L.S.Sec.alerts.sounds.main";

    public SettingVM Toggle { get; }
    public SettingVM Slider { get; }
    public SettingVM Choice { get; }
    public SettingVM Number { get; }

    private sealed class Never : IDebounceTimer
    {
        public void Restart(Action onElapsed) { }
        public void Stop() { }
    }

    /// <summary>A host that does nothing: nothing in a miniature opens.</summary>
    private sealed class Inert : ISettingsHost
    {
        public void Navigate(string pageId, string? itemId = null) { }
        public void Toast(string text) { }
        public bool Confirm(string title, string body, SafeAnswer safe = SafeAnswer.Cancel) => false;
        public string? PickFolder(string? initial) => null;
        public string? PickFile(string? initial, string filter) => null;
        public string? PickSaveFile(string defaultName, string filter) => null;
        public string? PickColor(string? initial) => null;
        public string? CaptureHotkey(string settingId, string? current) => null;
        public void Reload() { }
        public string? AskText(string title, string prompt, string initial) => null;
        public string? PickCharacter(string title, IReadOnlyList<string> names) => null;
        public bool? ConfirmChoice(string title, string body, string yesLabel, string noLabel, SafeAnswer safe = SafeAnswer.Cancel) => null;
        public System.Windows.Window? DialogOwner => null;
    }
}
