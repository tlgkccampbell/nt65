using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Answers the questions about block openers and directives that binding, layout and emission
/// all ask. These passes walk the same lines and must agree on the answers, so the answers live
/// in one place.
/// </summary>
public static class Constructs
{
    /// <summary>
    /// Determines whether a block repeats its contents, so that they are read once and emitted
    /// many times.
    /// </summary>
    public static bool Repeats(BlockKind kind) => kind is BlockKind.Repeat or BlockKind.Each;

    /// <summary>
    /// Returns the condition and the message of an <c>.assert</c> or an <c>.error</c>. The
    /// condition is the expression that has to hold, and it is null for an <c>.error</c>.
    /// </summary>
    public static Assertion AssertionOf(StatementSyntax directive) => directive switch
    {
        AssertDirectiveSyntax assert => new Assertion(assert.Condition, MessageOf(assert.Message)),
        ErrorDirectiveSyntax error => new Assertion(null, MessageOf(error.Message)),
        _ => default,
    };

    /// <summary>
    /// Returns the segment that a segment block or a region line names, or null when the line is
    /// neither or names no segment. A name in quotes has already been reported, but it still
    /// names its segment.
    /// </summary>
    public static string? SegmentOf(StatementSyntax opener) =>
        opener is SegmentStatementSyntax segment ? SegmentNames.Of(segment.Name) : null;

    // The message is null when the line has none. An `.assert` without a message leaves that
    // child position empty, and an `.error` without one holds a missing token where the string
    // belongs.
    private static string? MessageOf(SyntaxToken? message) =>
        message is { IsMissing: false } written ? Literals.Text(written.Text) : null;

    /// <summary>Represents what an <c>.assert</c> or an <c>.error</c> requires.</summary>
    /// <param name="Condition">The expression that has to hold, or null for an <c>.error</c>.</param>
    /// <param name="Message">The message to report, or null when the directive has none.</param>
    public readonly record struct Assertion(ExpressionSyntax? Condition, string? Message);
}
