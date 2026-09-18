using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

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
    /// Where a line writes one of ca65's unnamed labels, or -1. One is defined by a <c>:</c>
    /// at the start of a line and named by <c>:+</c> or <c>:-</c> where an operand begins.
    /// A <c>:</c> after a name is a label or an address-size prefix, which is what <c>z:foo</c>
    /// and <c>a:-1</c> stay.
    /// </summary>
    public static int UnnamedLabel(ImmutableArray<GreenToken> tokens)
    {
        if (tokens[0].Kind == SyntaxKind.Colon)
            return 0;
        for (var i = 1; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Kind == SyntaxKind.Colon
                && tokens[i + 1].Kind is SyntaxKind.Plus or SyntaxKind.Minus
                && tokens[i - 1].Kind is not (SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.CheapLocal))
            {
                return i;
            }
        }
        return -1;
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

    /// <summary>
    /// Whether the line is <c>.segment NAME</c> with neither a brace nor a size: a region line,
    /// which places what follows it rather than what is inside it.
    /// </summary>
    public static bool IsRegion(ImmutableArray<GreenToken> tokens)
    {
        if (tokens[0].Kind != SyntaxKind.Directive
            || !tokens[0].Text.Equals(".segment", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (var token in tokens)
        {
            if (token.Kind is SyntaxKind.Colon or SyntaxKind.OpenBrace)
                return false;
        }
        return true;
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

        // `.export .proc init {` opens the block the declaration after `.export` does.
        if (kind == LineKind.Directive && tokens[0].Text.Equals(".export", StringComparison.OrdinalIgnoreCase)
            && tokens[1].Kind == SyntaxKind.Directive)
        {
            start = 1;
        }
        var token = tokens[start];
        if (token.Kind == SyntaxKind.Directive)
            return DataBlockKind(tokens, start) ?? SyntaxFacts.BlockKindOfDirective(token.Text);
        if (token.Kind == SyntaxKind.Identifier)
        {
            var next = tokens[start + 1].Kind;
            if (next == SyntaxKind.Bang || (kind == LineKind.BlockClose && next == SyntaxKind.OpenBrace))
                return BlockKind.MacroBlock;
        }
        return BlockKind.Unknown;
    }

    /// <summary>
    /// What a block of data holds, which decides how its lines read. <c>.data name {</c> is
    /// mixed data; an element type with a count, <c>.byte[] {</c>, holds values; and
    /// <c>.type T {</c> with no count holds one record's <c>member = value</c> lines. Null for
    /// a line that opens no data.
    /// </summary>
    private static BlockKind? DataBlockKind(ImmutableArray<GreenToken> tokens, int start)
    {
        var element = start;
        if (tokens[start].Text.Equals(".data", StringComparison.OrdinalIgnoreCase))
        {
            element = -1;
            for (var i = start + 1; i < tokens.Length; i++)
            {
                if (tokens[i].Kind == SyntaxKind.Colon)
                {
                    element = i + 1;
                    break;
                }
            }
            if (element < 0 || element >= tokens.Length)
                return BlockKind.Data;
        }

        var directive = tokens[element];
        var record = directive.Text.Equals(".type", StringComparison.OrdinalIgnoreCase);
        if (directive.Kind != SyntaxKind.Directive || (!record && SyntaxFacts.ElementSize(directive.Text) is null))
            return null;
        for (var i = element + 1; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == SyntaxKind.OpenBracket)
                return BlockKind.DataBody;
        }
        return record ? BlockKind.RecordInitializer : null;
    }
}
