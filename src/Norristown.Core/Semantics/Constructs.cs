using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Which constructs the stages so far implement, and what a block opener says. Binding,
/// layout and emission all walk the same lines and must agree about which of them they are
/// ready to read.
/// </summary>
public static class Constructs
{
    /// <summary>
    /// Whether a block's contents belong to a layer that is not built yet. Their lines parse
    /// and are kept, but nothing resolves names in them, sizes them or writes them out.
    /// </summary>
    public static bool IsDeferred(BlockKind kind) => kind is BlockKind.Macro or BlockKind.MacroBlock;

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

    /// <summary>Whether a data directive is a <c>.tag</c>, which declares an instance of a type.</summary>
    public static bool IsTag(SyntaxNode? statement) =>
        statement is { Kind: SyntaxKind.DataDirective, ChildTokens.Length: > 0 }
        && statement.ChildTokens[0].Text.Equals(".tag", StringComparison.OrdinalIgnoreCase);

    /// <summary>The type expression of a <c>.tag</c>, which is its first operand.</summary>
    public static SyntaxNode? TagTypeOf(SyntaxNode? statement) =>
        IsTag(statement) ? statement!.ChildNodes.FirstOrDefault() : null;

    /// <summary>What an <c>.assert</c> or an <c>.error</c> asks for.</summary>
    /// <param name="Condition">What has to hold, or null for an <c>.error</c>.</param>
    /// <param name="Level">How much a failure matters.</param>
    /// <param name="Message">What to say about it, or null when none was written.</param>
    public readonly record struct Assertion(SyntaxNode? Condition, Severity Level, string? Message);

    /// <summary>
    /// The segment a block opener names: a standard name for a shortcut directive, or
    /// the name in quotes. Null when the line does not open a segment block or does not say.
    /// </summary>
    public static string? SegmentOf(SyntaxNode opener)
    {
        if (opener.Kind != SyntaxKind.SegmentBlock)
            return null;
        if (opener.ChildTokens.Length > 0 && SegmentNames.Shortcut(opener.ChildTokens[0].Text) is { } standard)
            return standard;
        foreach (var token in opener.ChildTokens)
        {
            if (token.Kind == SyntaxKind.StringLiteral)
                return SegmentNames.Unquote(token.Text);
        }
        return null;
    }
}
