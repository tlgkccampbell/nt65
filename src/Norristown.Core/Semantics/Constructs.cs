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
    public static bool IsDeferred(BlockKind kind) => kind is BlockKind.Macro or BlockKind.MacroBlock
        or BlockKind.If or BlockKind.Repeat or BlockKind.Each or BlockKind.Enum or BlockKind.Struct
        or BlockKind.Union or BlockKind.Charmap or BlockKind.List or BlockKind.TagInitializer;

    /// <summary>
    /// The segment a block opener names (§5.2): a standard name for a shortcut directive, or
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
