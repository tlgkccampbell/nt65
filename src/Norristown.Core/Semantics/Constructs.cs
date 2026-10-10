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
    /// Returns the condition and the message of an <c>.assert</c>, an <c>.error</c> or a
    /// <c>.warning</c>. The condition is the expression that has to hold, and it is null for an
    /// <c>.error</c> or a <c>.warning</c>.
    /// </summary>
    /// <param name="directive">The <c>.assert</c>, <c>.error</c> or <c>.warning</c> line.</param>
    /// <param name="text">
    /// A function that returns the text a message stands for, or null when it stands for no text.
    /// The message is an expression, so only the caller can evaluate it. When the function is null,
    /// the directive has no message.
    /// </param>
    public static Assertion AssertionOf(StatementSyntax directive, Func<ExpressionSyntax, string?>? text = null) =>
        new(
            (directive as AssertDirectiveSyntax)?.Condition,
            MessageOf(directive) is { } message ? text?.Invoke(message) : null);

    /// <summary>
    /// Returns the message of an <c>.assert</c>, an <c>.error</c> or a <c>.warning</c>, or null
    /// when the line has none or is none of these. The three take a message by one rule, so it may
    /// be text in quotes, a text constant or a call that returns text.
    /// </summary>
    public static ExpressionSyntax? MessageOf(StatementSyntax directive) =>
        directive switch
        {
            AssertDirectiveSyntax assert => assert.Message,
            ErrorDirectiveSyntax error => error.Message,
            _ => null,
        };

    /// <summary>
    /// Returns the segment that a segment block or a region line names, or null when the line is
    /// neither or names no segment. A name in quotes has already been reported, but it still
    /// names its segment.
    /// </summary>
    public static string? SegmentOf(StatementSyntax opener) =>
        opener is SegmentStatementSyntax segment ? SegmentNames.Of(segment.Name) : null;

    /// <summary>Represents what an <c>.assert</c>, an <c>.error</c> or a <c>.warning</c> requires.</summary>
    /// <param name="Condition">
    /// The expression that has to hold, or null for an <c>.error</c> or a <c>.warning</c>.
    /// </param>
    /// <param name="Message">The message to report, or null when the directive has none.</param>
    public readonly record struct Assertion(ExpressionSyntax? Condition, string? Message);
}
