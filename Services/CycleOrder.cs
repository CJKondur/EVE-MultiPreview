using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>The order the next / previous client keys walk the clients in (Home › Switching › Cycle order): the profile's
/// CycleOrder first, in its order, then every other client in the order the caller gives (window order). Nobody drops out:
/// a client the list leaves out is still reached, after the listed ones. An empty list leaves the order as it is.
/// Names match whatever their case. Shared by both apps (ThumbnailManager.CycleAll, Linux Previews.Cycle).</summary>
public static class CycleOrder
{
    /// <summary><paramref name="clients"/> reordered by <paramref name="order"/>. <paramref name="nameOf"/> gives a client's
    /// character ("" or null at the login screen, which no list names). Stable: clients the list does not name keep their
    /// order among themselves, and two clients of one name (never in practice) keep theirs.</summary>
    public static List<T> Apply<T>(IReadOnlyList<T> clients, Func<T, string?> nameOf, IReadOnlyList<string>? order)
    {
        if (order == null || order.Count == 0) return clients.ToList();
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++)
            if (!string.IsNullOrWhiteSpace(order[i])) rank.TryAdd(order[i].Trim(), i);
        return clients
            .Select((c, i) => (c, i, r: nameOf(c) is { Length: > 0 } n && rank.TryGetValue(n, out var at) ? at : int.MaxValue))
            .OrderBy(x => x.r).ThenBy(x => x.i)
            .Select(x => x.c).ToList();
    }

    /// <summary>Whether the profile's list already names <paramref name="name"/>, whatever its case.</summary>
    public static bool Has(Profile p, string name) =>
        p.CycleOrder.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds <paramref name="name"/> at the end; false when it is blank or already listed.</summary>
    public static bool Add(Profile p, string name)
    {
        name = name?.Trim() ?? "";
        // A comma is the file's separator (AutoProfile.Join); EVE names have none.
        if (name.Length == 0 || name.Contains(AutoProfile.Separator) || Has(p, name)) return false;
        p.CycleOrder.Add(name);
        return true;
    }

    /// <summary>Takes <paramref name="name"/> off the list, whatever its case.</summary>
    public static void Remove(Profile p, string name) =>
        p.CycleOrder.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Moves <paramref name="name"/> to where <paramref name="target"/> stands (null: to the end), as a group's
    /// members are dragged (MergedGroups.MoveMember). Nothing happens for a name not listed.</summary>
    public static void Move(Profile p, string name, string? target)
    {
        int from = p.CycleOrder.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;
        var moved = p.CycleOrder[from];
        int to = target == null ? p.CycleOrder.Count - 1
            : p.CycleOrder.FindIndex(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase));
        if (to < 0 || to == from) return;
        p.CycleOrder.RemoveAt(from);
        p.CycleOrder.Insert(to, moved);
    }

    /// <summary>One step up (earlier, <paramref name="up"/>) or down; nothing at either end.</summary>
    public static void Step(Profile p, string name, bool up)
    {
        int from = p.CycleOrder.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        int to = up ? from - 1 : from + 1;
        if (from < 0 || to < 0 || to >= p.CycleOrder.Count) return;
        (p.CycleOrder[from], p.CycleOrder[to]) = (p.CycleOrder[to], p.CycleOrder[from]);
    }
}
