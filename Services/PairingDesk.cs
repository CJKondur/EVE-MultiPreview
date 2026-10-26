using System;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>What the pairing desk says to a program's <c>pair</c>: open a window now, one is already open, wait out the pause
/// after a Deny, or no more windows in this run.</summary>
public enum PairStep { Ask, Asking, Wait, Blocked }

/// <summary>Plan 39 Ruling 6: the rate limit of the Allow/Deny window, a pure state machine on a monotonic clock handed in
/// (<c>now</c>; App's is <see cref="Environment.TickCount64"/>). One window at a time; a pause of <see cref="CoolDown"/> after a
/// Deny or a timeout (none after Allow); at most <see cref="MaxPrompts"/> windows per run of MultiPreview, then
/// <see cref="PairStep.Blocked"/> until it restarts. A window that never reports its answer expires as a Deny once
/// <see cref="PromptLife"/> has passed, so the desk can never stay "asking". Used on the window's thread only.</summary>
public sealed class PairingDesk
{
    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PromptLife = TimeSpan.FromSeconds(120);
    public const int MaxPrompts = 3;

    private int _prompts;
    private TimeSpan? _openedAt;
    private TimeSpan _waitUntil = TimeSpan.MinValue;

    /// <summary>A program asks: <see cref="PairStep.Ask"/> (the caller opens the window; it counts as a prompt) only when no
    /// window is open, fewer than <see cref="MaxPrompts"/> were shown and no pause is running.</summary>
    public PairStep Request(TimeSpan now)
    {
        Expire(now);
        if (_openedAt != null) return PairStep.Asking;
        if (_prompts >= MaxPrompts) return PairStep.Blocked;
        if (now < _waitUntil) return PairStep.Wait;
        _prompts++;
        _openedAt = now;
        return PairStep.Ask;
    }

    /// <summary>The open window was answered (a timeout is <paramref name="allowed"/> false). Deny starts the pause; Allow
    /// does not. Without an open window (the desk already expired it) nothing changes and the answer is <c>false</c>: an Allow
    /// that arrives after the desk's deadline must not turn anything on. True only when a window was open and is now closed.</summary>
    public bool Answered(bool allowed, TimeSpan now)
    {
        Expire(now);
        if (_openedAt == null) return false;
        _openedAt = null;
        if (!allowed) _waitUntil = now + CoolDown;
        return true;
    }

    /// <summary>How long the open window may still stay up: <see cref="PromptLife"/> counted from the moment the desk opened it
    /// (not from when the window got on screen), never below zero; zero when none is open. The window's timer uses it, so it
    /// closes at the desk's deadline.</summary>
    public TimeSpan Remaining(TimeSpan now)
    {
        if (_openedAt is not { } opened) return TimeSpan.Zero;
        var left = opened + PromptLife - now;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>The hello reply's pairing word: <c>asking</c> while a window is open, <c>blocked</c> after the last window of
    /// the run, <c>wait</c> during a pause, else <c>ready</c>.</summary>
    public string Status(TimeSpan now)
    {
        Expire(now);
        if (_openedAt != null) return "asking";
        if (_prompts >= MaxPrompts) return "blocked";
        if (now < _waitUntil) return "wait";
        return "ready";
    }

    private void Expire(TimeSpan now)
    {
        if (_openedAt is not { } opened || now < opened + PromptLife) return;
        _openedAt = null;
        _waitUntil = opened + PromptLife + CoolDown;
    }
}

/// <summary>Plan 39 Rulings 3 and 7: the words of the Allow/Deny window. The program is named by its image's file name (from
/// the kernel's PID, never from anything it sent); its folder is a second line only while streamer mode is off, because a
/// path can hold the Windows user name. Both are only ever arguments of string.Format.</summary>
public static class PairingPrompt
{
    public static (string Title, string Body, string? From) Text(string? imagePath, bool elevatedBoth, bool streamer, Func<string, string, string> str)
    {
        var file = string.IsNullOrEmpty(imagePath) ? "" : Path.GetFileName(imagePath);
        var program = file.Length > 0 ? file : str("L.Pair.UnknownProgram", "A program");
        var body = elevatedBoth
            ? str("L.Pair.BodyElevated", BodyElevatedEnglish)
            : str("L.Pair.Body", BodyEnglish);
        string? from = null;
        if (!streamer && file.Length > 0 && Path.GetDirectoryName(imagePath) is { Length: > 0 } folder)
            from = string.Format(str("L.Pair.From", "From: {0}"), folder);
        return (str("L.Pair.Title", "Let a program control MultiPreview?"), string.Format(body, program), from);
    }

    private const string BodyEnglish = "{0} asks to send commands to MultiPreview: switch clients and profiles and the other commands of the command list. It never sends keys to EVE. Only programs of this Windows account can ask. You can turn this off any time under Settings › App settings › General.";
    private const string BodyElevatedEnglish = BodyEnglish + "\n\nMultiPreview runs as administrator: Allow also lets your ordinary programs send these commands.";
}
