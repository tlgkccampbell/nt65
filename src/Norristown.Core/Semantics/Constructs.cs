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
    /// for an <c>.error</c>), how much it matters, and the message written with it.
    /// </summary>
    public static Assertion AssertionOf(SyntaxNode directive)
    {
        var level = Severity.Error;
        string? message = null;
        foreach (var token in directive.ChildTokens)
        {
            if (token.Kind == SyntaxKind.Identifier && SyntaxFacts.IsAssertLevel(token.Text))
                level = token.Text.StartsWith('w') || token.Text.StartsWith("ldw", StringComparison.OrdinalIgnoreCase)
                    ? Severity.Warning
                    : Severity.Error;
            else if (token.Kind == SyntaxKind.StringLiteral)
                message ??= Literals.Text(token.Text);
        }
        return new Assertion(directive.ChildNodes.FirstOrDefault(), level, message);
    }

    /// <summary>What an <c>.assert</c> or an <c>.error</c> asks for.</summary>
    /// <param name="Condition">What has to hold, or null for an <c>.error</c>.</param>
    /// <param name="Level">How much a failure matters.</param>
    /// <param name="Message">What to say about it, or null when none was written.</param>
    public readonly record struct Assertion(SyntaxNode? Condition, Severity Level, string? Message);

    /// <summary>
    /// The segment a segment block or a region line names, or null when the line is neither or
    /// names none. A name written in quotes has been reported, and still names its segment.
    /// </summary>
    public static string? SegmentOf(SyntaxNode opener) =>
        opener.Kind is SyntaxKind.SegmentBlock or SyntaxKind.SegmentRegion or SyntaxKind.SegmentDeclaration
            && opener.ChildTokens.Length > 1
            ? SegmentNames.Of(opener.ChildTokens[1])
            : null;
}
