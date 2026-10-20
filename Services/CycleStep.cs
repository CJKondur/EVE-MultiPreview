namespace EveMultiPreview.Services;

/// <summary>The one index step of the cycle keys (IDEAS P79 (4)), for a list of clients that the key could go to: where
/// the next press goes, counted from the active client's place in it. No window in it.</summary>
public static class CycleStep
{
    /// <summary>The place in the list a press goes to.</summary>
    /// <param name="currentIdx">The active client's place in the list, or -1 when the list does not hold it (the player is in
    /// a client the key's group leaves out, or in no client at all).</param>
    /// <param name="count">How many clients the list holds. At least one: the keys return before this with none.</param>
    /// <param name="forward">The forward key, or the backward one.</param>
    /// <returns>From a client in the list, the next one (the previous one backward), wrapping round. From outside it, the
    /// first (the forward key), or the last (the backward key): the two keys mirror each other. Counting back from
    /// "place -1" went to the last but one. -1 when the list is empty.</returns>
    public static int Next(int currentIdx, int count, bool forward)
    {
        if (count <= 0) return -1;
        if (forward) return (currentIdx + 1) % count;
        return currentIdx < 0 ? count - 1 : (currentIdx - 1 + count) % count;
    }
}
