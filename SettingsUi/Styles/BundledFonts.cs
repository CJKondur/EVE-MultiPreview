using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using FontFamily = System.Windows.Media.FontFamily;

namespace EveMultiPreview.SettingsUi;

/// <summary>The typefaces MultiPreview carries for its looks (UI styles spec §11.1; the owner's decision of 2026-10-12:
/// "Bundle the eleven fonts"): eleven families under the SIL Open Font License 1.1, 29 static files, each family's licence
/// beside its files, all compiled into the app under Resources/Fonts/&lt;Folder&gt;. The files are used unchanged: no
/// subsetting, no renaming.</summary>
public static class BundledFonts
{
    /// <param name="Name">The family's name, as its files name it (after the '#' of a look's font).</param>
    /// <param name="Folder">Its folder under Resources/Fonts.</param>
    /// <param name="Files">Its files, one per weight.</param>
    /// <param name="Weights">Their OpenType weights, in the same order.</param>
    /// <param name="Cyrillic">Whether it draws Russian (spec §13: six do not).</param>
    /// <param name="Copyright">The copyright line of its licence.</param>
    public sealed record Family(string Name, string Folder, IReadOnlyList<string> Files, IReadOnlyList<int> Weights, bool Cyrillic, string Copyright);

    /// <summary>The eleven, in the spec's order.</summary>
    public static IReadOnlyList<Family> All { get; } = new Family[]
    {
        new("Barlow", "Barlow", new[] { "Barlow-Regular.ttf", "Barlow-Medium.ttf", "Barlow-SemiBold.ttf" }, new[] { 400, 500, 600 }, false,
            "Copyright 2017 The Barlow Project Authors (https://github.com/jpt/barlow)"),
        new("IBM Plex Sans", "IBMPlexSans", new[] { "IBMPlexSans-Regular.ttf", "IBMPlexSans-SemiBold.ttf" }, new[] { 400, 600 }, true,
            "Copyright © 2017 IBM Corp. with Reserved Font Name \"Plex\""),
        new("IBM Plex Mono", "IBMPlexMono", new[] { "IBMPlexMono-Regular.ttf", "IBMPlexMono-Medium.ttf" }, new[] { 400, 500 }, true,
            "Copyright © 2017 IBM Corp. with Reserved Font Name \"Plex\""),
        new("Figtree", "Figtree", new[] { "Figtree-Regular.ttf", "Figtree-Medium.ttf", "Figtree-SemiBold.ttf", "Figtree-Bold.ttf" }, new[] { 400, 500, 600, 700 }, false,
            "Copyright 2022 The Figtree Project Authors (https://github.com/erikdkennedy/figtree)"),
        new("Rubik", "Rubik", new[] { "Rubik-Regular.ttf", "Rubik-Medium.ttf", "Rubik-SemiBold.ttf" }, new[] { 400, 500, 600 }, true,
            "Copyright 2015 The Rubik Project Authors (https://github.com/googlefonts/rubik)"),
        new("Overpass", "Overpass", new[] { "Overpass-Regular.ttf", "Overpass-SemiBold.ttf", "Overpass-Bold.ttf" }, new[] { 400, 600, 700 }, true,
            "Copyright 2021 The Overpass Project Authors (https://github.com/RedHatOfficial/Overpass)"),
        new("Overpass Mono", "OverpassMono", new[] { "OverpassMono-Regular.ttf", "OverpassMono-SemiBold.ttf" }, new[] { 400, 600 }, true,
            "Copyright 2021 The Overpass Project Authors (https://github.com/RedHatOfficial/Overpass)"),
        new("Saira", "Saira", new[] { "Saira-Regular.ttf", "Saira-SemiBold.ttf" }, new[] { 400, 600 }, false,
            "Copyright 2020 The Saira Project Authors (https://github.com/Omnibus-Type/Saira)"),
        new("Saira Condensed", "SairaCondensed", new[] { "SairaCondensed-Bold.ttf" }, new[] { 700 }, false,
            "Copyright 2020 The Saira Project Authors (https://github.com/Omnibus-Type/Saira)"),
        new("DM Sans", "DMSans", new[] { "DMSans-Regular.ttf", "DMSans-Medium.ttf", "DMSans-SemiBold.ttf", "DMSans-Bold.ttf" }, new[] { 400, 500, 600, 700 }, false,
            "Copyright 2014 The DM Sans Project Authors (https://github.com/googlefonts/dm-fonts)"),
        new("Archivo", "Archivo", new[] { "Archivo-Regular.ttf", "Archivo-Medium.ttf", "Archivo-Bold.ttf" }, new[] { 400, 500, 700 }, false,
            "Copyright 2020 The Archivo Project Authors (https://github.com/Omnibus-Type/Archivo)"),
    };

    /// <summary>The base a look's font path is relative to.</summary>
    public static readonly Uri Base = new("pack://application:,,,/EVE MultiPreview;component/");

    public static Family? Find(string name) => All.FirstOrDefault(f => f.Name == name);

    /// <summary>The family as WPF draws it, from the files compiled in.</summary>
    public static FontFamily FamilyOf(Family family) => new(Base, "./Resources/Fonts/" + family.Folder + "/#" + family.Name);

    /// <summary>A file compiled into the app (WPF's resources, "resources/fonts/&lt;folder&gt;/&lt;file&gt;"), read without an
    /// Application object (tests, and the menus' fonts before any window).</summary>
    public static Stream Open(string path)
    {
        var assembly = typeof(BundledFonts).Assembly;
        var resources = new ResourceManager(assembly.GetName().Name + ".g", assembly);
        return resources.GetStream(path.Replace('\\', '/').ToLowerInvariant()) ?? throw new FileNotFoundException("not compiled in: " + path);
    }

    /// <summary>The family's licence, as its project published it.</summary>
    public static string Licence(Family family)
    {
        using var reader = new StreamReader(Open("resources/fonts/" + family.Folder + "/OFL.txt"));
        return reader.ReadToEnd();
    }
}
