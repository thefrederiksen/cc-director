namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Pure, UI-free helpers for assembling dictation transcript text. Kept separate
/// from <see cref="SpeakDialog"/> so the accumulation behaviour can be unit-tested
/// without spinning up an Avalonia window.
/// </summary>
public static class DictationText
{
    /// <summary>
    /// Joins two transcript fragments with exactly one separating space, unless
    /// either side already supplies the boundary whitespace. This underpins the
    /// "Resume appends new speech to the (edited) transcript" behaviour: the
    /// left side is whatever the user currently has in the review box and the
    /// right side is the freshly cleaned segment, so the user's edits are never
    /// rewritten - only extended.
    /// </summary>
    public static string Join(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return right ?? "";
        if (string.IsNullOrEmpty(right)) return left;
        var leftEndsWithSpace = char.IsWhiteSpace(left[^1]);
        var rightStartsWithSpace = char.IsWhiteSpace(right[0]);
        if (leftEndsWithSpace || rightStartsWithSpace) return left + right;
        return left + " " + right;
    }

    /// <summary>
    /// Insert <paramref name="insert"/> into <paramref name="existing"/> at index
    /// <paramref name="caret"/>, adding exactly one separating space on a side only when the adjacent
    /// character is not already whitespace - so the inserted words never smush against the surrounding
    /// text. An out-of-range caret is clamped to the end; an empty insert returns
    /// <paramref name="existing"/> unchanged. This is the pure, box-free core shared by the desktop
    /// Insert button and the fire-and-forget Send, so both drop dictation at the caret identically.
    /// </summary>
    public static string InsertAt(string existing, int caret, string insert)
    {
        existing ??= "";
        if (string.IsNullOrEmpty(insert)) return existing;
        if (caret < 0 || caret > existing.Length) caret = existing.Length;
        var prefix = existing[..caret];
        var suffix = existing[caret..];
        var needsSpaceBefore = prefix.Length > 0 && !char.IsWhiteSpace(prefix[^1]);
        var needsSpaceAfter = suffix.Length > 0 && !char.IsWhiteSpace(suffix[0]);
        var mid = (needsSpaceBefore ? " " : "") + insert + (needsSpaceAfter ? " " : "");
        return prefix + mid + suffix;
    }
}
