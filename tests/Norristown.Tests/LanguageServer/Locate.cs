using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one this helper means.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Finds positions in a test source by the text written there. A test that names what it
/// probes, as in <c>Locate.At(Source, "lda |ptr")</c>, says what it is about, and keeps probing
/// the same place when lines are added to the source above it.
/// </summary>
internal static class Locate
{
    /// <summary>
    /// Returns the position of the <paramref name="occurrence"/>th match of
    /// <paramref name="find"/> in <paramref name="text"/>. A <c>|</c> in
    /// <paramref name="find"/> marks the column within the match; without one, the position
    /// is where the match starts. Throws if there is no such match.
    /// </summary>
    public static Position At(string text, string find, int occurrence = 1) =>
        Match(text, find, occurrence).Caret;

    /// <summary>
    /// Returns the range from the <c>|</c> in <paramref name="find"/>, or from the start of
    /// the match when there is none, to the end of the <paramref name="occurrence"/>th match
    /// in <paramref name="text"/>.
    /// </summary>
    public static Range Span(string text, string find, int occurrence = 1)
    {
        var (caret, end) = Match(text, find, occurrence);
        return new Range(caret, end);
    }

    private static (Position Caret, Position End) Match(string text, string find, int occurrence)
    {
        var bar = find.IndexOf('|', StringComparison.Ordinal);
        var plain = bar < 0 ? find : find.Remove(bar, 1);
        var start = -1;
        for (var i = 0; i < occurrence; i++)
        {
            start = text.IndexOf(plain, start + 1, StringComparison.Ordinal);
            if (start < 0)
                throw new ArgumentException($"the text has no match {occurrence} for \"{plain}\"", nameof(find));
        }
        return (PositionOf(text, start + Math.Max(0, bar)), PositionOf(text, start + plain.Length));
    }

    private static Position PositionOf(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return new Position(line, offset - lineStart);
    }
}
