using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Questions about block openers and directives that binding, layout and emission all ask.
/// They walk the same lines and must agree on the answers, so the answers live in one place.
/// </summary>
public static class Constructs
{
    /// <summary>Whether a block repeats its contents, so that they are read once and written many times.</summary>
    public static bool Repeats(BlockKind kind) => kind is BlockKind.Repeat or BlockKind.Each;

    /// <summary>
    /// What an <c>.assert</c> or an <c>.error</c> says: the expression that has to hold (none,
    /// for an <c>.error</c>), and the message written with it.
    /// </summary>
    public static Assertion AssertionOf(StatementSyntax directive) => directive switch
    {
        AssertDirectiveSyntax assert => new Assertion(assert.Condition, MessageOf(assert.Message)),
        ErrorDirectiveSyntax error => new Assertion(null, MessageOf(error.Message)),
        _ => default,
    };

    /// <summary>
    /// The segment a segment block or a region line names, or null when the line is neither or
    /// names none. A name written in quotes has been reported, and still names its segment.
    /// </summary>
    public static string? SegmentOf(StatementSyntax opener) =>
        opener is SegmentStatementSyntax segment ? SegmentNames.Of(segment.Name) : null;

    // A message the line does not write is null: an `.assert` without one leaves the slot
    // empty, and an `.error` without one holds a missing token where the string belongs.
    private static string? MessageOf(SyntaxToken? message) =>
        message is { IsMissing: false } written ? Literals.Text(written.Text) : null;

    /// <summary>What an <c>.assert</c> or an <c>.error</c> asks for.</summary>
    /// <param name="Condition">What has to hold, or null for an <c>.error</c>.</param>
    /// <param name="Message">What to say about it, or null when none was written.</param>
    public readonly record struct Assertion(ExpressionSyntax? Condition, string? Message);
}
