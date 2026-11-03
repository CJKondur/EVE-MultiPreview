using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Simple;

namespace EveMultiPreview.Linux;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        new Previews().Start();
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Keeps one thumbnail per EVE client: looks for clients every 2 s, redraws every 100 ms, and marks the
/// client in front. Prints the average time a redraw of all thumbnails takes every 10 s.</summary>
public sealed class Previews
{
    private const int ThumbWidth = 320, ThumbHeight = 180, Gap = 16;
    private readonly EveClients _clients = new();
    private readonly Dictionary<uint, ThumbnailWindow> _thumbs = new();
    private int _ticks;
    private double _drawMs;

    public void Start()
    {
        Console.WriteLine($"EVE MultiPreview for Linux: looking for windows titled \"{_clients.TitlePrefix}…\" (Composite: {(_clients.HasComposite ? "yes" : "no, covered clients will not show")}; scaling on the X server: {(_clients.ScalesOnServer ? "yes" : "no")})");
        new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick()).Start();
        Tick();
    }

    private void Tick()
    {
        if (_ticks++ % 20 == 0) Sync();
        var watch = Stopwatch.StartNew();
        uint active = _clients.Active();
        foreach (var thumb in _thumbs.Values)
        {
            thumb.Update(_clients);
            thumb.ClientInFront = thumb.Client.Window == active;
        }
        _drawMs += watch.Elapsed.TotalMilliseconds;
        if (_ticks % 100 == 0)
        {
            Console.WriteLine($"{_thumbs.Count} thumbnail(s), {_drawMs / 100:F1} ms per redraw");
            _drawMs = 0;
        }
    }

    private void Sync()
    {
        var found = _clients.Find();
        foreach (var gone in _thumbs.Keys.Except(found.Select(f => f.Window)).ToList())
        {
            _thumbs[gone].Close();
            _thumbs.Remove(gone);
            _clients.Forget(gone);
            Console.WriteLine($"Client closed: window {gone}");
        }
        foreach (var client in found.Where(f => !_thumbs.ContainsKey(f.Window)))
        {
            var thumb = new ThumbnailWindow(client, ThumbWidth, ThumbHeight);
            thumb.Clicked += c => _clients.Activate(c.Window);
            thumb.Position = new PixelPoint(Gap + _thumbs.Count * (ThumbWidth + Gap), Gap);
            _thumbs[client.Window] = thumb;
            thumb.Show();
            Console.WriteLine($"Client found: {client.Character} (window {client.Window})");
        }
    }
}
