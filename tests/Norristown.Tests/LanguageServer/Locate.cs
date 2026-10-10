using Norristown.LanguageServer.Protocol;

// The protocol defines its own Range type, and this helper uses that one.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Finds positions in a test source by searching for the text at them. A test that names the
/// text it probes, as in <c>Locate.At(Source, "lda |ptr")</c>, shows what it is about, and it
/// keeps probing the same place when lines are added to the source above it.
/// </summary>
internal static class Locate
{
    /// <summary>
    /// Gets a range covering a whole file, as a client sends when it asks for the actions across
    /// all of it.
    /// </summary>
    public static Range Whole { get; } = new(new Position(0, 0), new Position(1000, 0));

    /// <summary>
    /// Returns the position of the <paramref name="occurrence"/>th match of
    /// <paramref name="find"/> in <paramref name="text"/>. A <c>|</c> in
    /// <paramref name="find"/> marks the column within the match; without one, the position
    /// is where the match starts. Throws if there is no such match.
    /// </summary>
    public static Position At(string text, string find, int occurrence = 1) =>
        Match(text, find, occurrence).Caret;

    /// <summary>
    /// Returns the empty range at the position <see cref="At"/> returns, which is how a request
    /// names a caret with nothing selected.
    /// </summary>
    public static Range Caret(string text, string find, int occurrence = 1)
    {
        var at = At(text, find, occurrence);
        return new Range(at, at);
    }

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

    /// <summary>Returns the line and column of the character at <paramref name="offset"/> in <paramref name="text"/>.</summary>
    public static Position PositionOf(string text, int offset)
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
}
