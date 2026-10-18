using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using EveMultiPreview.Services;
using FontFamily = System.Windows.Media.FontFamily;

// The app's own resources are its neutral ones: no satellite assembly is looked for on a resource read.
[assembly: NeutralResourcesLanguage("en")]

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
    public static Stream Open(string path) =>
        Compiled.GetStream(path.Replace('\\', '/').ToLowerInvariant()) ?? throw new FileNotFoundException("not compiled in: " + path);

    /// <summary>The app's compiled-in files, read through one manager (S3F-m4: a new one on every read).</summary>
    private static readonly ResourceManager Compiled = new(typeof(BundledFonts).Assembly.GetName().Name + ".g", typeof(BundledFonts).Assembly);

    private static readonly object MenuGate = new();
    /// <summary>One collection a family: GDI+ reads a collection's families once, so a family added to a collection already
    /// read is not seen (measured while writing plan S3: the second family came back missing).</summary>
    private static readonly Dictionary<string, System.Drawing.Text.PrivateFontCollection> MenuFonts = new();

    /// <summary>A GDI+ font of a carried family for the WinForms menus (spec §11.1, "the menus"), or null when the family is
    /// not carried or Windows refused its files (the menu then draws in Segoe UI, O18). WinForms cannot see a WPF resource
    /// font: the family's files are added to this process alone, from the bytes compiled in, for GDI+ (the menu's Font) and
    /// for GDI (which draws its text). Nothing is installed or written. Their memory stays for the process's life, as GDI+
    /// and GDI read the fonts there.</summary>
    public static System.Drawing.Font? MenuFont(string family, float size)
    {
        if (Find(family) is not { } carried) return null;
        try
        {
            lock (MenuGate)
            {
                if (!MenuFonts.TryGetValue(family, out var collection))
                {
                    // Kept only once its family is in it (final review M-1: an empty collection kept after one transient
                    // GDI+ failure made the menus Segoe UI for the rest of the run). A refused load is tried again on a
                    // later call, up to Tries times a run, not on every menu for ever.
                    if (Failures.GetValueOrDefault(family) >= Tries) return null;
                    collection = Load(carried);
                    if (collection == null)
                    {
                        Failures[family] = Failures.GetValueOrDefault(family) + 1;
                        return null;
                    }
                    MenuFonts[family] = collection;
                }
                var loaded = collection.Families.FirstOrDefault(f => f.Name == family);
                return loaded == null ? null : new System.Drawing.Font(loaded, size);
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            Refused(e);
            return null;
        }
    }

    /// <summary>How many times a run a family's files are tried for the menus before it is given up (M-1).</summary>
    public const int Tries = 3;

    /// <summary>Refused loads a family, while it has no collection kept.</summary>
    private static readonly Dictionary<string, int> Failures = new();

    /// <summary>Tests only: runs before a family's files are added to the process; an exception it throws is a refusal.</summary>
    public static Action<string>? Loading { get; set; }

    /// <summary>Tests only: forgets a family's kept collection and its refusals, so the next call loads it again. The
    /// collection is disposed here, at once: left to the finalizer, its GdipDeletePrivateFontCollection ran on the finalizer
    /// thread at any moment, also while a later load of the same family was under way (IDEAS P107). A menu font made from it
    /// is the caller's: a test disposes its own first.</summary>
    public static void Forget(string family)
    {
        lock (MenuGate)
        {
            if (MenuFonts.Remove(family, out var collection)) collection.Dispose();
            Failures.Remove(family);
        }
    }

    /// <summary>Tests only: the collection kept for a family, or null.</summary>
    public static System.Drawing.Text.PrivateFontCollection? Kept(string family)
    {
        lock (MenuGate) return MenuFonts.GetValueOrDefault(family);
    }

    /// <summary>The latest refusal, "Type: message", for a test's failure message (the error log has the first only).</summary>
    public static string? LastRefusal { get; private set; }

    /// <summary>A new collection holding the family's files, or null when Windows refused one or the family is not in it.</summary>
    private static System.Drawing.Text.PrivateFontCollection? Load(Family carried)
    {
        var collection = new System.Drawing.Text.PrivateFontCollection();
        try
        {
            Loading?.Invoke(carried.Name);
            foreach (var file in carried.Files) AddToProcess(collection, "resources/fonts/" + carried.Folder + "/" + file);
            var families = collection.Families;
            if (families.Any(f => f.Name == carried.Name)) return collection;
            Refused(new System.Runtime.InteropServices.ExternalException(carried.Name + " is not in its own files' collection (GDI+ listed "
                + families.Length + ": " + string.Join(", ", families.Select(f => f.Name)) + ")"));
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            Refused(e);
        }
        collection.Dispose();   // no font was made from it
        return null;
    }

    private static bool _refusalLogged;

    /// <summary>Windows refused a carried font for the menus: written to the error log once a run (the menus then draw in
    /// Segoe UI), not once a menu. System.Drawing keeps no GDI+ status number: what is recorded is the exception's type and
    /// HResult (font names only, never a player's text).</summary>
    private static void Refused(Exception e)
    {
        var what = e.GetType().Name + " 0x" + e.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ": " + e.Message;
        LastRefusal = what;
        if (_refusalLogged) return;
        _refusalLogged = true;
        ErrorLog.Shared.Record(new InvalidOperationException(what), "UI styles", DateTime.Now);
    }

    private static void AddToProcess(System.Drawing.Text.PrivateFontCollection collection, string path)
    {
        using var stream = Open(path);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var memory = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, memory, bytes.Length);
            collection.AddMemoryFont(memory, bytes.Length);
        }
        catch
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(memory);   // GDI+ did not take it: nothing reads it
            throw;
        }
        uint fonts = 0;
        if (AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, ref fonts) == IntPtr.Zero)
            Refused(new System.ComponentModel.Win32Exception("Windows refused " + path + " for GDI (AddFontMemResourceEx)"));
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, ref uint fonts);

    /// <summary>The family's licence, as its project published it.</summary>
    public static string Licence(Family family)
    {
        using var reader = new StreamReader(Open("resources/fonts/" + family.Folder + "/OFL.txt"));
        return reader.ReadToEnd();
    }
}
