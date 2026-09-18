using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Which constructs the stages so far implement, and what a block opener says. Binding,
/// layout and emission all walk the same lines and must agree about which of them they are
/// ready to read.
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

    /// <summary>What an <c>.assert</c> or an <c>.error</c> asks for.</summary>
    /// <param name="Condition">What has to hold, or null for an <c>.error</c>.</param>
    /// <param name="Message">What to say about it, or null when none was written.</param>
    public readonly record struct Assertion(ExpressionSyntax? Condition, string? Message);

    /// <summary>
    /// The segment a segment block or a region line names, or null when the line is neither or
    /// names none. A name written in quotes has been reported, and still names its segment.
    /// </summary>
    public static string? SegmentOf(StatementSyntax opener) =>
        opener is SegmentStatementSyntax { Name: { } name } ? SegmentNames.Of(name) : null;

    private static string? MessageOf(SyntaxToken? message) =>
        message is { } written ? Literals.Text(written.Text) : null;
}
