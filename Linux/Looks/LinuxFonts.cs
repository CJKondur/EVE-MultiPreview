using Avalonia.Media;

namespace EveMultiPreview.Linux;

/// <summary>The typefaces the looks use, on Linux. The eleven families MultiPreview carries are the Windows app's own
/// files (Resources/Fonts/&lt;Folder&gt;, SIL Open Font License 1.1), compiled into the Linux app unchanged as Avalonia
/// resources at the same paths, each family's licence beside it as an embedded resource ("Font.&lt;Folder&gt;\OFL.txt").
/// A family of Windows (Segoe UI, Cascadia Mono, Consolas) is not on Linux: the desktop's own sans-serif or monospace
/// stands in. Glyphs a family lacks (Cyrillic for six of them, Chinese, Japanese, Korean) come from the desktop's
/// fonts, which Avalonia finds per character.</summary>
internal static class LinuxFonts
{
    /// <param name="Name">The family's name, after the '#' of a look's font.</param>
    /// <param name="Folder">Its folder under Resources/Fonts.</param>
    /// <param name="Copyright">The copyright line of its licence.</param>
    public sealed record Family(string Name, string Folder, string Copyright);

    // Copy of SettingsUi/Styles/BundledFonts.cs:All (that file is WPF) — keep in sync; LinuxLooksTests compares them.
    public static IReadOnlyList<Family> All { get; } = new Family[]
    {
        new("Barlow", "Barlow", "Copyright 2017 The Barlow Project Authors (https://github.com/jpt/barlow)"),
        new("IBM Plex Sans", "IBMPlexSans", "Copyright © 2017 IBM Corp. with Reserved Font Name \"Plex\""),
        new("IBM Plex Mono", "IBMPlexMono", "Copyright © 2017 IBM Corp. with Reserved Font Name \"Plex\""),
        new("Figtree", "Figtree", "Copyright 2022 The Figtree Project Authors (https://github.com/erikdkennedy/figtree)"),
        new("Rubik", "Rubik", "Copyright 2015 The Rubik Project Authors (https://github.com/googlefonts/rubik)"),
        new("Overpass", "Overpass", "Copyright 2021 The Overpass Project Authors (https://github.com/RedHatOfficial/Overpass)"),
        new("Overpass Mono", "OverpassMono", "Copyright 2021 The Overpass Project Authors (https://github.com/RedHatOfficial/Overpass)"),
        new("Saira", "Saira", "Copyright 2020 The Saira Project Authors (https://github.com/Omnibus-Type/Saira)"),
        new("Saira Condensed", "SairaCondensed", "Copyright 2020 The Saira Project Authors (https://github.com/Omnibus-Type/Saira)"),
        new("DM Sans", "DMSans", "Copyright 2014 The DM Sans Project Authors (https://github.com/googlefonts/dm-fonts)"),
        new("Archivo", "Archivo", "Copyright 2020 The Archivo Project Authors (https://github.com/Omnibus-Type/Archivo)"),
    };

    /// <summary>The families of Windows the looks name that draw fixed-width text.</summary>
    private static readonly HashSet<string> WindowsMono = new(StringComparer.OrdinalIgnoreCase) { "Cascadia Mono", "Cascadia Code", "Consolas" };

    /// <summary>The desktop's fixed-width families, in the order Avalonia tries them (fontconfig's "monospace" last).</summary>
    public const string Monospace = "DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, monospace";

    /// <summary>The prefix of a carried family in a look file: "/EVE MultiPreview;component/Resources/Fonts/&lt;Folder&gt;/#&lt;Name&gt;".</summary>
    private const string Component = ";component/";

    /// <summary>The Avalonia resource URI of a carried family's folder and name ("avares://eve-multipreview/Resources/Fonts/Rubik#Rubik").</summary>
    public static string Uri(string folder, string name) => $"avares://{Assembly}/Resources/Fonts/{folder}#{name}";

    private static readonly string Assembly = typeof(LinuxFonts).Assembly.GetName().Name ?? "eve-multipreview";

    /// <summary>A look file's font text as Avalonia draws it: a carried family from the app's resources, a list of Windows
    /// families as the desktop's monospace or its default font.</summary>
    public static FontFamily FamilyOf(string written) => Resolve(written) is { } name ? new FontFamily(name) : FontFamily.Default;

    /// <summary>The Avalonia name of a look file's font text, or null for the desktop's default font.</summary>
    public static string? Resolve(string written)
    {
        var text = written.Trim();
        int component = text.IndexOf(Component, StringComparison.Ordinal);
        int hash = text.IndexOf('#');
        if (component >= 0 && hash > component)
        {
            var path = text[(component + Component.Length)..hash].TrimEnd('/');   // "Resources/Fonts/Rubik"
            var folder = path[(path.LastIndexOf('/') + 1)..];
            return Uri(folder, text[(hash + 1)..]);
        }
        var names = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Any(WindowsMono.Contains) ? Monospace : null;
    }

    /// <summary>The licence text of a carried family, as it came with its files; empty when it is not embedded.</summary>
    public static string Licence(Family family)
    {
        // The resource is named after the folder it was built from: "Font.Rubik\OFL.txt" from Windows, "Font.Rubik/OFL.txt" from Linux.
        var assembly = typeof(LinuxFonts).Assembly;
        using var stream = assembly.GetManifestResourceStream("Font." + family.Folder + "\\OFL.txt") ?? assembly.GetManifestResourceStream("Font." + family.Folder + "/OFL.txt");
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
