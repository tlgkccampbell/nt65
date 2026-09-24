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
            _ when IsMacroCall(tokens, 0) => LineKind.MacroCall,
            SyntaxKind.Identifier when second == SyntaxKind.EndOfLine => LineKind.BareIdentifier,
            SyntaxKind.Mnemonic => LineKind.Instruction,
            _ => LineKind.Expression,
        };
    }

    /// <summary>
    /// Returns the index of the token where one of ca65's unnamed labels appears on the line, or
    /// -1 if none does. An unnamed label is defined by a <c>:</c> at the start of a line and
    /// referred to by <c>:+</c> or <c>:-</c> in an operand. A <c>:</c> right after a name is a
    /// label or an address-size prefix instead, so <c>z:foo</c> and <c>a:-1</c> keep those
    /// meanings.
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
    /// Returns whether the line opens a block, by ending with <c>{</c>, and whether it closes one,
    /// by starting with <c>}</c>. A <c>{</c> inside a parenthesis still open on the line does not
    /// open a block, so a half-typed <c>m!({</c> does not swallow the lines after it.
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
    /// Returns a value indicating whether the line is a <c>.segment NAME</c> region line that
    /// opens no braced block. Such a line puts the lines after it in the segment, rather than
    /// lines inside braces.
    /// </summary>
    public static bool IsRegion(ImmutableArray<GreenToken> tokens) =>
        tokens[0].DirectiveKind == DirectiveKind.Segment && SegmentForm(tokens, opens: false) == SyntaxKind.SegmentRegion;

    /// <summary>
    /// Returns the kind of node a <c>.segment</c> line parses to. A line that opens a block with
    /// <c>{</c> is a <see cref="SyntaxKind.SegmentBlock"/>. Otherwise a line with a <c>:</c> is a
    /// <see cref="SyntaxKind.SegmentDeclaration"/>, and any other line is a
    /// <see cref="SyntaxKind.SegmentRegion"/>, even one with a stray <c>{</c> the parser reports.
    /// </summary>
    /// <param name="tokens">The line's tokens.</param>
    /// <param name="opens">Whether the line opens a block, as <see cref="Braces"/> decides.</param>
    public static SyntaxKind SegmentForm(ImmutableArray<GreenToken> tokens, bool opens)
    {
        if (opens)
            return SyntaxKind.SegmentBlock;
        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxKind.Colon)
                return SyntaxKind.SegmentDeclaration;
        }
        return SyntaxKind.SegmentRegion;
    }

    /// <summary>
    /// Returns a value indicating whether the token at <paramref name="at"/> names a macro being
    /// called, as in <c>name!(...)</c> or <c>name!</c>. A macro may be named after an instruction,
    /// such as one another processor has. Then only <c>!(</c> after it makes a call, because
    /// <c>lda !flag</c> is an instruction whose operand is the logical not of <c>flag</c>.
    /// </summary>
    public static bool IsMacroCall(ImmutableArray<GreenToken> tokens, int at)
    {
        if (at + 1 >= tokens.Length || tokens[at + 1].Kind != SyntaxKind.Bang)
            return false;
        return tokens[at].Kind switch
        {
            SyntaxKind.Identifier => true,
            SyntaxKind.Mnemonic => at + 2 < tokens.Length && tokens[at + 2].Kind == SyntaxKind.OpenParen,
            _ => false,
        };
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

        // `.export .proc init {` opens the same kind of block as the declaration after `.export`.
        if (kind == LineKind.Directive && tokens[0].DirectiveKind == DirectiveKind.Export
            && tokens[1].Kind == SyntaxKind.Directive)
        {
            start = 1;
        }
        var token = tokens[start];
        if (token.Kind == SyntaxKind.Directive)
            return DataBlockKind(tokens, start) ?? SyntaxFacts.BlockKindOf(token.DirectiveKind);
        if (IsMacroCall(tokens, start))
            return BlockKind.MacroBlock;

        // `} else {` closes one block argument of a macro call and opens the next.
        if (kind == LineKind.BlockClose && token.Kind == SyntaxKind.Identifier
            && tokens[start + 1].Kind == SyntaxKind.OpenBrace)
        {
            return BlockKind.MacroBlock;
        }
        return BlockKind.Unknown;
    }

    /// <summary>
    /// Returns the kind of block a data line opens, which decides how the block's lines are
    /// parsed, or null for a line that opens no data block. <c>.data name {</c> is mixed data. An
    /// element type with a count, such as <c>.byte[] {</c>, holds an array's values, and
    /// <c>.type T {</c> with no count holds one record's <c>member = value</c> lines.
    /// </summary>
    private static BlockKind? DataBlockKind(ImmutableArray<GreenToken> tokens, int start)
    {
        var element = start;
        if (tokens[start].DirectiveKind == DirectiveKind.Data)
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

        var directive = tokens[element].DirectiveKind;
        if (!SyntaxFacts.IsElementType(directive))
            return null;
        var counted = false;
        for (var i = element + 1; i < tokens.Length && !counted; i++)
            counted = tokens[i].Kind == SyntaxKind.OpenBracket;
        return SyntaxFacts.DataBodyKind(directive, counted);
    }
}
