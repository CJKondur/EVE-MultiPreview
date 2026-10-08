using System;
using System.Collections.Generic;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

public enum SettingKind { Toggle, Choice, Segmented, Slider, Number, Text, Folder, File, Color, Hotkey }

/// <summary>One option of a Choice or Segmented setting. LabelKey is an L.* resource key;
/// Literal, when set, is shown instead (language names, percentages: never translated).</summary>
public sealed record ChoiceOption(object Value, string LabelKey)
{
    public string? Literal { get; init; }
}

/// <summary>One setting card (spec §8.2). Build these with the <see cref="Setting"/> factory.</summary>
public sealed record SettingDescriptor
{
    /// <summary>Stable id, "page.section.name" style (e.g. "app.general.language"). Resource keys derive from it.</summary>
    public required string Id { get; init; }
    public required SettingKind Kind { get; init; }
    public required Func<AppSettings, object?> Get { get; init; }
    public required Action<AppSettings, object?> Set { get; init; }

    /// <summary>"AppSettings.X" / "Profile.X" names this setting writes. The coverage test counts
    /// them, and RegistryChecks proves Set writes nothing else.</summary>
    public required IReadOnlyList<string> Binds { get; init; }

    /// <summary>What must re-apply after a change. Never None: use Save for a setting that is
    /// read where it is used (spec §11.1 "read at runtime").</summary>
    public ApplyScope Apply { get; init; } = ApplyScope.Save;

    /// <summary>Extra live effect after the value is stored. Calls existing app logic only (spec §8.2).</summary>
    public Action<SettingsContext>? OnChanged { get; init; }

    /// <summary>The card shows only while this returns true (spec §6 conditional rows).</summary>
    public Func<AppSettings, bool>? ShowIf { get; init; }

    /// <summary>Shown on Home in Simple mode (spec D2).</summary>
    public bool Simple { get; init; }

    /// <summary>Old settings-window x:Names and handler names this card replaces (parity test).</summary>
    public IReadOnlyList<string> Replaces { get; init; } = Array.Empty<string>();

    /// <summary>Hotkey cards: the id the key is known by for clashes and the conflict check.
    /// Setting.Hotkey sets the bound property ("AppSettings.SuspendHotkey"), so the same key on two
    /// pages counts once; a character card uses "character:&lt;name&gt;". Null = the descriptor id.</summary>
    public string? UseId { get; init; }

    /// <summary>Get/Set touch state outside AppSettings (registry, files). Tests never call Set.</summary>
    public bool External { get; init; }

    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;

    /// <summary>L.* key of a unit shown after a number or slider value ("px", "ms", "%").</summary>
    public string? UnitKey { get; init; }

    public IReadOnlyList<ChoiceOption> Options { get; init; } = Array.Empty<ChoiceOption>();

    /// <summary>Open-file dialog filter for File settings ("WAV files|*.wav").</summary>
    public string FileFilter { get; init; } = "All files|*.*";

    /// <summary>Returns the L.* key of an error for a value that must not be stored, or null.
    /// The message may use {0} and {1} for Min and Max (spec §8.5).</summary>
    public Func<object?, string?>? Validate { get; init; }

    public string LabelKey => "L.S." + Id;
    public string DescKey => "L.S." + Id + ".Desc";
}
