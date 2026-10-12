using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace EveMultiPreview.Services;

/// <summary>The texts of one Windows notification (IDEAS #33), already in the player's language: the character's
/// name, the event (with its count), where it is (a system, or where the others went), and the two buttons.</summary>
public sealed record NotificationTexts(string Title, string Line, string? Detail, string SwitchLabel, string SnoozeLabel);

/// <summary>One notification as Windows is handed it. <paramref name="Banner"/>: it pops up; without it, it only
/// replaces its entry in the Notification Center.</summary>
public sealed record NotificationContent(string Tag, string Xml, bool Banner, DateTime ExpiresUtc);

/// <summary>One thing to ask of Windows: show (or replace) a notification, take one away by its tag, or take all of
/// MultiPreview's away.</summary>
public sealed record NotifierWork(NotifierWork.Kind What, NotificationContent? Content = null, string? Tag = null)
{
    public enum Kind { Show, Remove, Clear }
}

/// <summary>The XML of a notification, and what a click on it says back (IDEAS #33). Every text is cleaned to one
/// line, cut, and written as text by System.Xml.Linq: a name can't add an element, an attribute or an action.</summary>
public static class NotificationXml
{
    /// <summary>The most characters one text of a notification holds.</summary>
    public const int MaxText = 120;

    /// <summary>What a click says: "go:" and a tag (the notification itself and its first button: switch to that
    /// client), or "nap:" and a tag (its second button: snooze). The tag is one MultiPreview made.</summary>
    public const string Go = "go:", Nap = "nap:";

    public static string Build(NotificationTexts texts, string tag)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            new XElement("text", Clean(texts.Title)), new XElement("text", Clean(texts.Line)));
        // Asked after cleaning: a detail made only of characters that are not shown would be an empty line.
        if (Clean(texts.Detail) is { Length: > 0 } detail) binding.Add(new XElement("text", detail));
        var toast = new XElement("toast", new XAttribute("launch", Go + tag),
            new XElement("visual", binding),
            new XElement("actions",
                new XElement("action", new XAttribute("content", Clean(texts.SwitchLabel)), new XAttribute("arguments", Go + tag)),
                new XElement("action", new XAttribute("content", Clean(texts.SnoozeLabel)), new XAttribute("arguments", Nap + tag))),
            // MultiPreview plays its own sound for an alert (or none, as the player set it): the notification is silent.
            new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>A text as a notification shows it: one line, cut, and without the characters XML can't hold. Where
    /// such a character is taken out no gap is left: not two spaces where it stood between two words, and no space
    /// at an end.</summary>
    private static string Clean(string? text)
    {
        var line = PlainText.OneLine(text, MaxText);
        var kept = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                kept.Append(c).Append(line[++i]);
                continue;
            }
            if (c < ' ' || c == '\uFFFE' || c == '\uFFFF' || char.IsSurrogate(c)) continue;
            if (c == ' ' && (kept.Length == 0 || kept[^1] == ' ')) continue;
            kept.Append(c);
        }
        if (kept.Length > 0 && kept[^1] == ' ') kept.Length--;
        return kept.ToString();
    }

    /// <summary>What a click's arguments mean: ("go", tag) or ("nap", tag). Anything else is nothing: the arguments
    /// come back from Windows, and only what MultiPreview wrote itself is read.</summary>
    public static (string Verb, string Tag)? Read(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return null;
        string? verb = arguments.StartsWith(Go, StringComparison.Ordinal) ? "go"
            : arguments.StartsWith(Nap, StringComparison.Ordinal) ? "nap" : null;
        if (verb == null) return null;
        string tag = arguments[(verb.Length + 1)..];
        return IsTag(tag) ? (verb, tag) : null;
    }

    /// <summary>A tag as MultiPreview makes them: "n" and up to twelve digits or small letters.</summary>
    public static bool IsTag(string tag) =>
        tag.Length is >= 2 and <= 13 && tag[0] == 'n' && tag.Skip(1).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z');
}

/// <summary>Windows notifications for alerts (IDEAS #33): which alerts get one, which entry a newer alert replaces,
/// when the banner pops up again, and when an entry is taken away. It keeps what it showed (at most twenty entries),
/// what it remembers of entries that left without their client being looked at (so that the banner rule holds for
/// them too), and a list of what Windows is still to be asked: at most one picture for each entry, and one removal
/// for each entry that was looked at or made room. Whoever talks to Windows takes that list with
/// <see cref="NextWork"/>. No window, no Windows and no clock in it: the time is the alert's own. Any thread.</summary>
public sealed class AlertNotifications
{
    /// <summary>Windows keeps this many entries per program. A further one takes the place of the entry that was
    /// updated longest ago.</summary>
    public const int MaxEntries = 20;

    /// <summary>Of this many entries that left without their client being looked at (they made room, or the player
    /// closed them by hand) the count and the banner time are remembered; beyond it the oldest are forgotten. It
    /// is ten times what can be shown: a pair that comes again within the banner's quiet time finds itself here
    /// unless more than this many other pairs came in between.</summary>
    private const int MaxRemembered = 200;

    /// <summary>The banner of one character and event pops up again at most this often. In between, a newer alert
    /// only replaces the entry.</summary>
    public static readonly TimeSpan BannerQuiet = TimeSpan.FromSeconds(30);

    /// <summary>How long after its last alert an entry stays before Windows removes it by itself.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromHours(12);

    /// <summary>How long the notification's second button snoozes its event for its character.</summary>
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(15);

    /// <summary>One entry: the alerts of one event for one character since the player last looked.</summary>
    public sealed record Entry(string Tag, string Character, string EventKey, string Severity, int Count, DateTime LastUtc, DateTime BannerUtc);

    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    // Entries that left without their client being looked at, oldest first: at most one for a character and event.
    private readonly List<Entry> _remembered = new();
    private readonly List<NotifierWork> _work = new();
    private long _tags;

    private static bool Same(Entry e, string character, string eventKey) =>
        string.Equals(e.Character, character, StringComparison.OrdinalIgnoreCase) && string.Equals(e.EventKey, eventKey, StringComparison.Ordinal);

    /// <summary>An alert was shown. True when Windows is to be asked something for it.</summary>
    /// <param name="on">Windows notifications are switched on.</param>
    /// <param name="from">From which severity (AlertWays.Reaches).</param>
    /// <param name="texts">The texts of an entry, in the player's language. Called here, on the alert's thread, and
    /// UNDER THE LEDGER'S LOCK: it must not call back into the ledger or into whoever owns it, and it must not wait
    /// for another thread (one that is waiting for this lock would never come). It may throw: then this alert has
    /// no notification, and nothing of it is kept.</param>
    public bool Raised(AlertNotice notice, bool on, int from, Func<Entry, NotificationTexts> texts)
    {
        if (!on || notice.ClientInFront || string.IsNullOrEmpty(notice.Character) || !AlertWays.Reaches(from, notice.Severity)) return false;
        var now = notice.TimeUtc;
        lock (_lock)
        {
            // First what the entry and its picture would be, and nothing is kept: the texts are the caller's function
            // and may throw. Kept before that, a failed alert would have used up the pair's banner for half a minute.
            int at = _entries.FindIndex(e => Same(e, notice.Character, notice.EventKey));
            Entry entry;
            bool banner;
            if (at >= 0)
            {
                var old = _entries[at];
                // A clock set back gives a negative time since the last banner: then the banner shows.
                var since = now - old.BannerUtc;
                banner = since < TimeSpan.Zero || since >= BannerQuiet;
                entry = old with { Severity = notice.Severity, Count = old.Count + 1, LastUtc = now, BannerUtc = banner ? now : old.BannerUtc };
            }
            else if (Remembered(notice.Character, notice.EventKey, now) is { } back)
            {
                // It made room for another, or the player closed it, and its banner is not half a minute old: it comes
                // back as another notification that counts on, and without a banner. Above twenty pairs every alert
                // would otherwise be a banner, and so would the next alert after a banner was closed by hand.
                entry = back with { Tag = NextTag(), Severity = notice.Severity, Count = back.Count + 1, LastUtc = now };
                banner = false;
            }
            else
            {
                entry = new Entry(NextTag(), notice.Character, notice.EventKey, notice.Severity, 1, now, now);
                banner = true;
            }
            // A picture of this entry that still waits is replaced where it stands; a banner it carried is not lost with it.
            int waiting = _work.FindIndex(w => w.What == NotifierWork.Kind.Show && w.Content!.Tag == entry.Tag);
            if (waiting >= 0) banner |= _work[waiting].Content!.Banner;
            var show = new NotifierWork(NotifierWork.Kind.Show,
                new NotificationContent(entry.Tag, NotificationXml.Build(texts(entry), entry.Tag), banner, now + Keep));

            // Now it is kept.
            if (at >= 0) _entries[at] = entry;
            else
            {
                _tags++;
                _remembered.RemoveAll(e => Same(e, notice.Character, notice.EventKey));    // taken back, or too old to be
                if (_entries.Count >= MaxEntries) RemoveOldest();
                _entries.Add(entry);
            }
            if (waiting >= 0) _work[waiting] = show;
            else _work.Add(show);
            return true;
        }
    }

    /// <summary>The tag the next new entry gets. Under the lock; the number is used up when the entry is kept.</summary>
    private string NextTag() => "n" + ToBase36(_tags + 1);

    /// <summary>What is remembered of a pair whose entry left without its client being looked at, while that still
    /// counts: its banner is less than the quiet time old. Null: nothing is remembered, the banner is older, or the
    /// clock was set back (then the pair starts afresh, with its banner). Under the lock; changes nothing.</summary>
    private Entry? Remembered(string character, string eventKey, DateTime now)
    {
        var entry = _remembered.Find(e => Same(e, character, eventKey));
        if (entry == null) return null;
        var since = now - entry.BannerUtc;
        return since >= TimeSpan.Zero && since < BannerQuiet ? entry : null;
    }

    /// <summary>An entry leaves without its client having been looked at: its count and its banner time are
    /// remembered. Under the lock.</summary>
    private void Remember(Entry entry)
    {
        _remembered.Add(entry);
        if (_remembered.Count > MaxRemembered) _remembered.RemoveAt(0);
    }

    /// <summary>Under the lock.</summary>
    private void RemoveOldest()
    {
        int oldest = 0;
        for (int i = 1; i < _entries.Count; i++)
            if (_entries[i].LastUtc < _entries[oldest].LastUtc) oldest = i;
        Remember(_entries[oldest]);
        TakeAway(_entries[oldest].Tag);
        _entries.RemoveAt(oldest);
    }

    /// <summary>Asks Windows to take one notification away, and drops a picture of it that still waits. Under the lock.</summary>
    private void TakeAway(string tag)
    {
        _work.RemoveAll(w => w.What == NotifierWork.Kind.Show && w.Content!.Tag == tag);
        _work.Add(new NotifierWork(NotifierWork.Kind.Remove, Tag: tag));
    }

    /// <summary>A click on a notification or one of its buttons: the entry it was about, once. Windows takes a
    /// clicked notification away itself, so nothing is asked of it. Null: not one of this session's, or clicked
    /// before.</summary>
    public Entry? Clicked(string tag) => Leave(tag, remember: false);

    /// <summary>The player closed a notification by hand: the entry it was about, once. Windows has taken it away
    /// itself, so nothing is asked of it. The notification is gone, but the pair's count and banner time are
    /// remembered as they are for an entry that made room: the banner that was just closed does not come back at
    /// the next alert, only when the quiet time since it is over. Null: not one of this session's, or gone already.</summary>
    public Entry? Dismissed(string tag) => Leave(tag, remember: true);

    private Entry? Leave(string tag, bool remember)
    {
        lock (_lock)
        {
            int at = _entries.FindIndex(e => e.Tag == tag);
            if (at < 0) return null;
            var entry = _entries[at];
            _entries.RemoveAt(at);
            _work.RemoveAll(w => w.What == NotifierWork.Kind.Show && w.Content!.Tag == tag);
            if (remember) Remember(entry);
            return entry;
        }
    }

    /// <summary>Whether a notification of this tag is one the ledger still has an entry for. Whoever has just shown
    /// one asks: a click, a look or a closing that came while Windows was being asked has taken the entry away, and
    /// the notification that arrived behind it would be one no click and no look could reach.</summary>
    public bool Holds(string tag)
    {
        lock (_lock) return _entries.Exists(e => e.Tag == tag);
    }

    /// <summary>A character's client came to the front, or is gone: its entries are taken away, and what is
    /// remembered of entries of its that left earlier is forgotten (its next alert starts afresh). True when Windows
    /// is to be asked something.</summary>
    public bool Looked(string character)
    {
        if (string.IsNullOrEmpty(character)) return false;
        lock (_lock)
        {
            _remembered.RemoveAll(e => string.Equals(e.Character, character, StringComparison.OrdinalIgnoreCase));
            var theirs = _entries.Where(e => string.Equals(e.Character, character, StringComparison.OrdinalIgnoreCase)).ToList();
            if (theirs.Count == 0) return false;
            foreach (var entry in theirs)
            {
                _entries.Remove(entry);
                TakeAway(entry.Tag);
            }
            return true;
        }
    }

    /// <summary>Windows notifications were switched off, or MultiPreview is closing: everything of MultiPreview's is
    /// taken away. True when Windows is to be asked: there was an entry, or work that waited.</summary>
    public bool Clear()
    {
        lock (_lock)
        {
            _remembered.Clear();
            if (_entries.Count == 0 && _work.Count == 0) return false;
            _entries.Clear();
            _work.Clear();
            _work.Add(new NotifierWork(NotifierWork.Kind.Clear));
            return true;
        }
    }

    /// <summary>The next thing to ask of Windows, or null.</summary>
    public NotifierWork? NextWork()
    {
        lock (_lock)
        {
            if (_work.Count == 0) return null;
            var next = _work[0];
            _work.RemoveAt(0);
            return next;
        }
    }

    public int Entries
    {
        get { lock (_lock) return _entries.Count; }
    }

    public int WorkWaiting
    {
        get { lock (_lock) return _work.Count; }
    }

    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var text = new StringBuilder();
        do
        {
            text.Insert(0, digits[(int)(value % 36)]);
            value /= 36;
        } while (value > 0);
        return text.ToString();
    }
}
