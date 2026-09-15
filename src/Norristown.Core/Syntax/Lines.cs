using System.Collections.Immutable;

namespace Norristown.Syntax;

internal static class Lines
{
    public static LineKind Classify(ImmutableArray<GreenToken> tokens)
    {
        var first = tokens[0].Kind;
        var second = tokens.Length > 1 ? tokens[1].Kind : SyntaxKind.EndOfLine;
        switch (first)
        {
            case SyntaxKind.EndOfLine:
                return LineKind.Blank;
            case SyntaxKind.Directive:
                return LineKind.Directive;
            case SyntaxKind.CloseBrace:
                return LineKind.BlockClose;
        }

        // A register or mnemonic in label or constant position still makes a label or a
        // constant: struct members and initializer values may use those names, and elsewhere
        // the reserved-word error is clearer than an unrecognized line.
        if (first is SyntaxKind.Identifier or SyntaxKind.CheapLocal or SyntaxKind.Register or SyntaxKind.Mnemonic)
        {
            if (second == SyntaxKind.Colon)
                return LineKind.Label;
            if (second == SyntaxKind.Equals)
                return LineKind.Constant;
        }
        return first switch
        {
            SyntaxKind.Identifier when second == SyntaxKind.Bang => LineKind.MacroCall,
            SyntaxKind.Identifier when second == SyntaxKind.EndOfLine => LineKind.BareIdentifier,
            SyntaxKind.Mnemonic => LineKind.Instruction,
            _ => LineKind.Expression,
        };
    }

    /// <summary>
    /// Whether the line opens and closes a block. A <c>{</c> inside a parenthesis still
    /// open on the line does not open one, so a half-typed <c>m!({</c> swallows nothing.
    /// </summary>
    public static (bool Opens, bool Closes) Braces(ImmutableArray<GreenToken> tokens)
    {
        var closes = tokens[0].Kind == SyntaxKind.CloseBrace;
        var last = tokens.Length - 2; // before EndOfLine
        if (last < 0 || tokens[last].Kind != SyntaxKind.OpenBrace)
            return (false, closes);
        var depth = 0;
        for (var i = 0; i < last; i++)
        {
            if (tokens[i].Kind == SyntaxKind.OpenParen)
                depth++;
            else if (tokens[i].Kind == SyntaxKind.CloseParen && depth > 0)
                depth--;
        }
        return (depth == 0, closes);
    }

    public static BlockKind BlockKindOf(ImmutableArray<GreenToken> tokens, LineKind kind)
    {
        // The statement that opens the block: after a label, or after the } of a continuation.
        var start = kind switch
        {
            LineKind.Label => 2,
            LineKind.BlockClose => 1,
            _ => 0,
        };
        var token = tokens[start];
        if (token.Kind == SyntaxKind.Directive)
            return SyntaxFacts.BlockKindOfDirective(token.Text);
        if (token.Kind == SyntaxKind.Identifier)
        {
            var next = tokens[start + 1].Kind;
            if (next == SyntaxKind.Bang || (kind == LineKind.BlockClose && next == SyntaxKind.OpenBrace))
                return BlockKind.MacroBlock;
        }
        return BlockKind.Unknown;
    }
}
