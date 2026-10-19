using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.Views;

/// <summary>
/// IDEAS #23: the bug report before it is saved. Every file of the zip is listed and its text shown; nothing is written
/// until Save as zip…, which asks where (ruling 18). The names switch builds the files again. Built in code, in the shape
/// of <see cref="HotkeySheetWindow"/>, so a look restyles it and the literal scan reads it. The files' text is shown,
/// never a format.
/// </summary>
public sealed class BugReportWindow : Window
{
    private readonly Func<bool, IReadOnlyList<BugReportFile>> _build;
    private readonly Func<Window, string?> _pickPath;
    private readonly Action<string> _saved;
    private readonly List<string> _texts = new();
    private readonly CheckBox _mask;
    private readonly ListBox _list;
    private readonly TextBox _text;
    private readonly TextBlock _status;
    private readonly Button _save;

    /// <param name="build">The files, names masked or not (BugReportSources.Gather in the app, fixtures in tests).</param>
    /// <param name="pickPath">Asks where the zip goes; null when cancelled.</param>
    /// <param name="saved">Told the path once the zip is written (the app opens its folder).</param>
    public BugReportWindow(Func<bool, IReadOnlyList<BugReportFile>> build, Func<Window, string?> pickPath, Action<string> saved)
    {
        _build = build;
        _pickPath = pickPath;
        _saved = saved;
        UiStyles.Attach(this);
        Title = Say("L.Report.Title", "Bug report", record: false);
        Width = 860;
        Height = 600;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Fx.BgBrush");
        UiFont.Apply(this, LocalizationService.CurrentLanguage);

        var dock = new DockPanel { Margin = new Thickness(16, 12, 16, 12), LastChildFill = true };
        void Add(UIElement e, Dock side) { DockPanel.SetDock(e, side); dock.Children.Add(e); }

        var top = new StackPanel();
        top.Children.Add(Line(Say("L.Report.Intro", "This is everything the zip will hold. Read it before you share it: names MultiPreview doesn't know, such as other players in your logs, are not hidden."), "Fx.TextBrush"));
        // Checked before the handlers are wired, so the files are built once here (ruling F10).
        _mask = new CheckBox { Content = Say("L.Report.MaskNames", "Hide character names"), IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        top.Children.Add(_mask);
        Add(top, System.Windows.Controls.Dock.Top);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(Line(Say("L.Report.Note", "Nothing is sent. The zip is saved where you choose, and you decide whom to give it to. Your Windows user name, the PC's name and your Discord webhook are always left out."), "Fx.TextSecondaryBrush"));
        _status = Line("", "Fx.TextBrush");
        _status.Margin = new Thickness(0, 6, 0, 0);
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        _save = new Button { Content = Say("L.Report.Save", "Save as zip…"), IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0) };
        _save.SetResourceReference(StyleProperty, "Fx.PrimaryButton");
        _save.Click += (_, _) => Save();
        var close = new Button { Content = Say("L.Report.Close", "Close"), IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_save);
        buttons.Children.Add(close);
        bottom.Children.Add(buttons);
        Add(bottom, System.Windows.Controls.Dock.Bottom);

        _text = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _text.SetResourceReference(FontFamilyProperty, "Fx.MonoFont");
        _list = new ListBox { Width = 220, Margin = new Thickness(0, 10, 10, 0) };
        _list.SelectionChanged += (_, _) => _text.Text = Shown;
        Add(_list, System.Windows.Controls.Dock.Left);
        dock.Children.Add(_text);

        var frame = new Border { Child = dock };
        frame.SetResourceReference(StyleProperty, "Fx.Dialog");
        Content = frame;

        Rebuild(0);
        _mask.Checked += (_, _) => Rebuild(_list.SelectedIndex);
        _mask.Unchecked += (_, _) => Rebuild(_list.SelectedIndex);
    }

    /// <summary>The files as they are now: what Save writes.</summary>
    public IReadOnlyList<BugReportFile> Files { get; private set; } = Array.Empty<BugReportFile>();

    /// <summary>The text of the file selected, "" when none is.</summary>
    public string Shown => _list.SelectedIndex is int i && i >= 0 && i < Files.Count ? Files[i].Text : "";

    /// <summary>Whether character names are hidden; setting it builds the files again (as the check box does).</summary>
    public bool MaskNames
    {
        get => _mask.IsChecked == true;
        set => _mask.IsChecked = value;
    }

    /// <summary>The status line: saved, could not save, could not make the report.</summary>
    public string Status => _status.Text;

    /// <summary>Every catalog text the window shows, in order (tests).</summary>
    public IReadOnlyList<string> Texts => _texts;

    /// <summary>Shows file <paramref name="index"/>.</summary>
    public void Select(int index) => _list.SelectedIndex = index;

    /// <summary>Asks where, then writes the zip there and only there. False when cancelled, when it could not be
    /// written, or when there is no report to write.</summary>
    public bool Save()
    {
        if (Files.Count == 0) return false;
        var path = _pickPath(this);
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            using (var stream = File.Create(path)) BugReport.WriteZip(stream, Files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = string.Format(LocalizationService.Str("L.Report.Failed", "Could not save the zip: {0}"), e.Message);
            return false;
        }
        _status.Text = string.Format(LocalizationService.Str("L.Report.Saved", "Saved as {0}"), Path.GetFileName(path));
        _saved(path);
        return true;
    }

    // The files once more, the same file selected. A report that could not be made leaves nothing to save (not the
    // files of the switch's other state) and says why; the window stays.
    private void Rebuild(int selected)
    {
        try
        {
            Files = _build(MaskNames);
            _status.Text = "";
        }
        catch (Exception e)
        {
            Files = Array.Empty<BugReportFile>();
            _status.Text = string.Format(LocalizationService.Str("L.Report.BuildFailed", "Could not make the report: {0}"), e.Message);
        }
        _list.Items.Clear();
        foreach (var file in Files) _list.Items.Add(file.Name);
        _save.IsEnabled = Files.Count > 0;
        _list.SelectedIndex = Files.Count == 0 ? -1 : Math.Clamp(selected, 0, Files.Count - 1);
        _text.Text = Shown;
    }

    private TextBlock Line(string text, string brushKey)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(StyleProperty, "Fx.Label");
        block.SetResourceReference(TextBlock.FontSizeProperty, "Fx.Size.Body");
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>The catalog's text, recorded as shown unless <paramref name="record"/> is off.</summary>
    private string Say(string key, string english, bool record = true)
    {
        var text = LocalizationService.Str(key, english);
        if (record) _texts.Add(text);
        return text;
    }
}
